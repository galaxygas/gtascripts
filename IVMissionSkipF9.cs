// IVMissionSkipF9.cs
// F9 passes the GTA IV / TLAD / TBoGT mission you are on by making the mission's own
// thread call the mission's own "mission passed" function. The rewards, story flags,
// news/phone unlocks, stats, "Mission Passed" screen and cleanup are the ones the mission
// script itself runs when you pass it normally. Nothing is faked from outside.
//
// INSTALL
//   ScriptHookDotNet (.NET Script Hook for GTA IV / EFLC / Complete Edition).
//   Copy this file into <game folder>\scripts\. It is compiled when the game starts.
//   While on a mission press F9. Progress and any reason for a refusal is shown on screen
//   and written to scripts\IVMissionSkipF9.log.
//
// HOW IT WORKS
//   GTA IV runs missions as RAGE script threads. The layout used here is:
//     - thread (GtaThread):  +04 id, +08 program hash, +0C state, +10 PC (offset into the
//       program code), +14 FP and +18 SP (indices into the thread stack), +28 wait time,
//       +44 stack size, +58 stack, +70 script name, +94 "on mission" byte (only used to
//       choose when several running scripts can pass).
//     - program (scrProgram): +04 name hash, +08 code, +10 code size.
//     - VM: CALL pushes the return PC and jumps; ENTER pushes FP and sets FP = SP - args - 2,
//       so a frame is [args][return PC][caller FP][locals]; LEAVE restores PC, FP, SP.
//       A thread blocked in WAIT keeps its PC on that NATIVE instruction with WAIT's
//       argument still on the stack and resumes from there.
//     - NATIVE operands in loaded code are handler addresses (resolved through the native
//       table), 1.0.7.0+ / EFLC / CE scripts use the 1.0.7 hash set, older ones Jenkins
//       one-at-a-time hashes of the native name.
//   Sources: scrDbg (github.com/ShinyWasabi/scrDbg, GTA IV CE VM reimplementation and thread
//   and program offsets), GTAIV.EFLC.FusionFix and IV-Network (thread layout), SC-CL and
//   SCO ToolBox (opcode encoding), GTAIVMissionScripts (decompiled IV, TLAD, TBoGT missions).
//
//   The thread list, program table and native table are found by structure: the static
//   objects in the game executable are validated against live threads (script name hashes
//   to the program hash) and against known native hashes, so no per-version addresses are
//   used. The mission thread's code is disassembled and its functions are analysed:
//     anchor   REGISTER_MISSION_PASSED (story/strand missions) or, when a script has none,
//              PLAYSTATS_MISSION_PASSED (random character missions).
//     pass     a function with no parameters on which every path reaches the anchor: the
//              native itself, a function that always reaches it, or one of the shared
//              mission-passed stat routines (those take parameters; stage and per-frame
//              functions that call the pass only under a condition take none).
//              Preferred: the innermost one that never returns (it ends with the mission
//              cleanup and TERMINATE_THIS_SCRIPT). Otherwise: the outermost one that
//              itself writes the mission's state (exit flag, final stage), preferring one
//              main() calls; it returns and the mission's main loop then runs the cleanup.
//   Checked against all decompiled IV, TLAD and TBoGT missions, including Rockstar's debug
//   "S = pass" blocks that are still in many scripts.
//
//   The call is injected while the mission thread is parked on a WAIT, exactly the way a
//   CALL instruction at that point would do it: the WAIT's PC is pushed as the return
//   address and PC is set to the pass function. If the pass function returns, the thread
//   goes back to the same WAIT with the same stack and carries on. Before anything is
//   written the thread's call stack is walked and every frame is checked (return address
//   follows a CALL to that frame's function, chain ends in main with return PC 0). If any
//   check fails nothing is written.
//   A pass function that returns is only injected while the thread is in main(), so the
//   mission's own main loop ends it right after.
//
// LIMITS
//   Missions without a pass function (side activities such as taxi, vigilante, races, dates,
//   friend outings without a pass) are reported as not skippable. So are the few missions
//   whose pass sequence is written inline in a stage switch (found in the decompiled scripts:
//   IV berniecar, brucie5m, finaleaorb, jimmy4a, pxdfcut, roman9; TLAD dave, elizabeta3,
//   ray1, stubbs3m1, stubbs3m2, stubbs3m5; TBoGT brother3, e2endcredits, mum1): F9
//   refuses there instead of registering a partial pass.
//   While a mission is already passing, failing or cleaning up, F9 does nothing.
//   During a long cutscene or phone call a "returning" pass waits until the mission is back
//   in main(); the skip keeps trying for 30 seconds.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using GTA;

public class IVMissionSkipF9 : Script
{
    const int TRY_MS = 30000;
    const int PREFER_MAIN_MS = 2500;
    const int FINISH_MS = 90000;

    // The GTA IV process itself; replaced only by offline tests.
    internal delegate IMemory MemoryOpener();
    internal static MemoryOpener OpenMemory = delegate { return new ProcessMemory(); };

    ScriptEngine engine;
    SkipJob job;
    int lastPress = -100000;

    public IVMissionSkipF9()
    {
        Interval = 0;
        BindKey(Keys.F9, new KeyPressDelegate(OnF9));
        this.Tick += new EventHandler(OnTick);
        Log.Write("loaded");
    }

    void OnF9()
    {
        int now = Game.GameTime;
        if (now - lastPress < 750) return;
        lastPress = now;
        try
        {
            StartSkip(now);
        }
        catch (Exception ex)
        {
            job = null;
            Say("Mission skip error, see scripts\\IVMissionSkipF9.log");
            Log.Write("F9 exception: " + ex);
        }
    }

    void OnTick(object sender, EventArgs e)
    {
        if (job == null) return;
        try
        {
            TickJob(Game.GameTime);
        }
        catch (Exception ex)
        {
            job = null;
            Say("Mission skip error, see scripts\\IVMissionSkipF9.log");
            Log.Write("tick exception: " + ex);
        }
    }

    void StartSkip(int now)
    {
        if (job != null)
        {
            Say(job.Injected ? "Mission is passing..." : "Skip already pending...");
            return;
        }
        if (!GTA.Native.Function.Call<bool>("GET_MISSION_FLAG"))
        {
            Say("Not on a mission");
            return;
        }
        if (engine == null)
        {
            ScriptEngine e = new ScriptEngine(OpenMemory());
            string why = e.Init();
            if (why != null)
            {
                Say("Mission skip unavailable: " + why);
                return;
            }
            engine = e;
        }

        string reason;
        MissionTarget t = engine.FindMission(out reason);
        if (t == null)
        {
            Say("Cannot skip: " + reason);
            return;
        }
        job = new SkipJob();
        job.Target = t;
        job.Started = now;
        Log.Write("pending skip of " + t.Name + " via sub_" + t.Pass.Entry.ToString("X") + (t.Pass.NoReturn ? " (ends script)" : " (returns to main)"));
        Say("Passing " + t.Name + "...");
        TickJob(now);
    }

    void TickJob(int now)
    {
        SkipJob j = job;
        if (!j.Injected)
        {
            if (!engine.ThreadIsSame(j.Target))
            {
                job = null;
                Say("Mission ended before the skip ran");
                return;
            }
            if (now - j.Started > TRY_MS)
            {
                job = null;
                Say("Could not skip right now (" + j.LastWhy + "). Press F9 again.");
                Log.Write("gave up: " + j.LastWhy);
                return;
            }
            if (!PlayerPlaying())
            {
                j.Note("player is not in control");
                return;
            }
            bool mainOnly = !j.Target.Pass.NoReturn || now - j.Started < PREFER_MAIN_MS;
            string why = engine.TryInject(j.Target, mainOnly);
            if (why == null)
            {
                j.Injected = true;
                j.InjectedAt = now;
                Say("Mission pass started: " + j.Target.Name);
                return;
            }
            if (why.StartsWith("!"))
            {
                job = null;
                Say("Cannot skip: " + why.Substring(1));
                Log.Write("refused: " + why.Substring(1));
                return;
            }
            j.Note(why);
            return;
        }

        bool alive = engine.ThreadIsSame(j.Target);
        bool onMission = GTA.Native.Function.Call<bool>("GET_MISSION_FLAG");
        if (!alive || !onMission)
        {
            job = null;
            Say("Mission passed: " + j.Target.Name);
            Log.Write("done: thread " + (alive ? "alive" : "ended") + ", mission flag " + onMission + ", " + (now - j.InjectedAt) + " ms");
            return;
        }
        if (now - j.InjectedAt > FINISH_MS)
        {
            job = null;
            Log.Write("pass function is running but the mission is still active after " + FINISH_MS + " ms");
            Say("Pass registered, mission script still running");
        }
    }

    static bool PlayerPlaying()
    {
        int id = GTA.Native.Function.Call<int>("GET_PLAYER_ID");
        return GTA.Native.Function.Call<bool>("IS_PLAYER_PLAYING", id);
    }

    static void Say(string msg)
    {
        Game.DisplayText(msg, 5000);
        Log.Write(msg);
    }
}

class SkipJob
{
    public MissionTarget Target;
    public int Started;
    public bool Injected;
    public int InjectedAt;
    public string LastWhy = "waiting";

    public void Note(string why)
    {
        if (why != LastWhy) Log.Write("waiting: " + why);
        LastWhy = why;
    }
}

static class Log
{
    public static void Write(string msg)
    {
        try
        {
            Directory.CreateDirectory("scripts");
            File.AppendAllText("scripts\\IVMissionSkipF9.log", DateTime.Now.ToString("HH:mm:ss.fff") + " " + msg + "\r\n");
        }
        catch { }
    }
}

// ------------------------------------------------------------------ memory access

struct MemRegion
{
    public uint Base;
    public uint Size;
    public MemRegion(uint b, uint s) { Base = b; Size = s; }
}

interface IMemory
{
    uint ModuleBase { get; }
    uint ModuleSize { get; }
    bool Read(uint address, byte[] buffer, int count);
    bool Write(uint address, byte[] data);
    // committed pages that can be written, inside the game executable
    List<MemRegion> WritableModuleRegions();
    // committed pages that can be read, whole address space, ascending
    List<MemRegion> ReadableRegions();
}

class ProcessMemory : IMemory
{
    uint modBase, modSize;

    public ProcessMemory()
    {
        ProcessModule m = Process.GetCurrentProcess().MainModule;
        modBase = (uint)U(m.BaseAddress);
        modSize = (uint)m.ModuleMemorySize;
    }

    public uint ModuleBase { get { return modBase; } }
    public uint ModuleSize { get { return modSize; } }

    static IntPtr P(uint a)
    {
        return IntPtr.Size == 4 ? new IntPtr(unchecked((int)a)) : new IntPtr((long)a);
    }

    // IntPtr.ToInt64 sign-extends 32-bit pointers above 2 GB
    static ulong U(IntPtr p)
    {
        return IntPtr.Size == 4 ? (ulong)(uint)p.ToInt32() : (ulong)p.ToInt64();
    }

    public bool Read(uint address, byte[] buffer, int count)
    {
        if (address < 0x10000 || count <= 0 || count > buffer.Length) return false;
        IntPtr read;
        return ReadProcessMemory(GetCurrentProcess(), P(address), buffer, new IntPtr(count), out read) && U(read) == (ulong)count;
    }

    public bool Write(uint address, byte[] data)
    {
        if (address < 0x10000) return false;
        IntPtr wrote;
        return WriteProcessMemory(GetCurrentProcess(), P(address), data, new IntPtr(data.Length), out wrote) && U(wrote) == (ulong)data.Length;
    }

    public List<MemRegion> WritableModuleRegions()
    {
        return Regions(modBase, (ulong)modBase + modSize, true);
    }

    public List<MemRegion> ReadableRegions()
    {
        return Regions(0x10000, 0x100000000UL, false);
    }

    List<MemRegion> Regions(ulong from, ulong to, bool writableOnly)
    {
        List<MemRegion> list = new List<MemRegion>();
        ulong addr = from;
        MEMORY_BASIC_INFORMATION info;
        int sz = Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION));
        while (addr < to)
        {
            if (U(VirtualQuery(P((uint)addr), out info, new IntPtr(sz))) == 0) break;
            ulong rb = U(info.BaseAddress);
            ulong rs = U(info.RegionSize);
            if (rs == 0 || rb + rs <= addr) break;
            uint prot = info.Protect & 0xFF;
            bool ok = info.State == 0x1000 && (info.Protect & 0x100) == 0 &&
                      (writableOnly ? (prot == 0x04 || prot == 0x08 || prot == 0x40 || prot == 0x80)
                                    : (prot == 0x02 || prot == 0x04 || prot == 0x08 || prot == 0x20 || prot == 0x40 || prot == 0x80));
            if (ok)
            {
                ulong b = Math.Max(rb, from);
                ulong e = Math.Min(rb + rs, to);
                if (e > b) list.Add(new MemRegion((uint)b, (uint)(e - b)));
            }
            addr = rb + rs;
        }
        return list;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [DllImport("kernel32.dll")]
    static extern IntPtr VirtualQuery(IntPtr lpAddress, out MEMORY_BASIC_INFORMATION lpBuffer, IntPtr dwLength);
    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, IntPtr nSize, out IntPtr lpNumberOfBytesRead);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, IntPtr nSize, out IntPtr lpNumberOfBytesWritten);
}

// ------------------------------------------------------------------ script engine

class MissionTarget
{
    public uint Thread;
    public uint Id;
    public uint Hash;
    public string Name;
    public uint CodeAddress;
    public ScoAnalysis Code;
    public ScoFunction Pass;
}

class ScriptEngine
{
    // GtaThread
    public const int T_ID = 0x04, T_HASH = 0x08, T_STATE = 0x0C, T_PC = 0x10, T_FP = 0x14, T_SP = 0x18;
    public const int T_WAIT = 0x28, T_STACKSIZE = 0x44, T_STACK = 0x58, T_NAME = 0x70, T_ONMISSION = 0x94;
    public const int T_READ = 0x98;
    // scrProgram
    public const int P_HASH = 0x04, P_CODE = 0x08, P_CODESIZE = 0x10;
    // scrThread::State
    public const uint S_RUNNING = 0, S_WAITING = 1, S_KILLED = 2;

    // 1.0.7.0+ / EFLC / CE native hashes (FusionFix natives.ixx, SCO ToolBox NativesNew) and
    // the older Jenkins hashes of the names (SCO ToolBox NativesOld, Aru's ScriptHook).
    public static readonly uint[] H_WAIT = { 0x266716AC, 0x7715C03B };
    public static readonly uint[] H_TERMINATE = { 0x2BCD1ECA, 0x245B6AB6 };
    public static readonly uint[] H_REGISTER_PASSED = { 0x5FBE5F52, 0x23E3421D };
    public static readonly uint[] H_PLAYSTATS_PASSED = { 0x437D3E19, 0xF94144F2 };

    IMemory mem;
    uint threadsArray;
    uint programsTable;
    uint nativesTable;
    public NativeIds Natives = new NativeIds();
    Dictionary<string, ScoAnalysis> analysisCache = new Dictionary<string, ScoAnalysis>();

    public ScriptEngine(IMemory m)
    {
        mem = m;
    }

    uint U32(uint address)
    {
        byte[] b = new byte[4];
        if (!mem.Read(address, b, 4)) return 0;
        return BitConverter.ToUInt32(b, 0);
    }

    bool TryU32(uint address, out uint v)
    {
        byte[] b = new byte[4];
        v = 0;
        if (!mem.Read(address, b, 4)) return false;
        v = BitConverter.ToUInt32(b, 0);
        return true;
    }

    bool InModule(uint a)
    {
        return a >= mem.ModuleBase && a - mem.ModuleBase < mem.ModuleSize;
    }

    public static uint Joaat(string s)
    {
        uint h = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c >= 'A' && c <= 'Z') c = (char)(c | 0x20);
            h += (byte)c;
            h += h << 10;
            h ^= h >> 6;
        }
        h += h << 3;
        h ^= h >> 11;
        h += h << 15;
        return h;
    }

    // rage::scrHash<T>::Lookup: { Entry* entries; u32 size; u32 defaultSize; u32 used }, Entry { u32 hash; T data; }
    bool HashLookup(uint table, uint hash, out uint value)
    {
        value = 0;
        uint entries, size;
        if (!TryU32(table, out entries) || !TryU32(table + 4, out size)) return false;
        return HashLookupRaw(entries, size, hash, 4096, out value);
    }

    bool HashLookupRaw(uint entries, uint size, uint hash, int maxProbes, out uint value)
    {
        value = 0;
        if (size == 0 || entries < 0x10000) return false;
        uint index = hash % size;
        uint probe = hash;
        uint cur;
        if (!TryU32(entries + index * 8, out cur)) return false;
        if (cur != hash)
        {
            int probes = 0;
            while (cur != 0)
            {
                if (++probes > maxProbes) return false;
                probe = (probe >> 1) + 1;
                index = (index + probe) % size;
                if (!TryU32(entries + index * 8, out cur)) return false;
                if (cur == hash) break;
            }
            if (cur == 0) return false;
        }
        return TryU32(entries + index * 8 + 4, out value);
    }

    public class ThreadInfo
    {
        public uint Address, Id, Hash, State;
        public string Name;
        public bool OnMission;
    }

    // Returns null on success, otherwise why the engine could not be located.
    public string Init()
    {
        Stopwatch clock = Stopwatch.StartNew();
        string why = Locate();
        Log.Write("engine scan took " + clock.ElapsedMilliseconds + " ms");
        return why;
    }

    uint[] mapBase, mapEnd;

    bool Mapped(uint a, uint len)
    {
        int lo = 0, hi = mapBase.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (a < mapBase[mid]) hi = mid - 1;
            else if (a >= mapEnd[mid]) lo = mid + 1;
            else return (ulong)a + len <= mapEnd[mid];
        }
        return false;
    }

    struct Candidate
    {
        public uint At, D0, D1;
        public Candidate(uint at, uint d0, uint d1) { At = at; D0 = d0; D1 = d1; }
    }

    string Locate()
    {
        List<MemRegion> readable = mem.ReadableRegions();
        mapBase = new uint[readable.Count];
        mapEnd = new uint[readable.Count];
        for (int i = 0; i < readable.Count; i++)
        {
            mapBase[i] = readable[i].Base;
            mapEnd[i] = (uint)Math.Min(0xFFFFFFFFUL, (ulong)readable[i].Base + readable[i].Size);
        }
        List<MemRegion> regions = mem.WritableModuleRegions();
        Log.Write("module 0x" + mem.ModuleBase.ToString("X8") + " size 0x" + mem.ModuleSize.ToString("X") + ", " +
                  regions.Count + " writable regions, " + readable.Count + " readable regions in process");
        List<Candidate> arrays = new List<Candidate>();
        List<Candidate> hashes = new List<Candidate>();
        byte[] buf = new byte[0x40000 + 16];
        for (int r = 0; r < regions.Count; r++)
        {
            MemRegion reg = regions[r];
            for (uint off = 0; off < reg.Size; off += 0x40000)
            {
                uint len = Math.Min(0x40000u + 16u, reg.Size - off);
                if (len < 16) break;
                if (!mem.Read(reg.Base + off, buf, (int)len)) continue;
                uint stop = Math.Min(0x40000u, len - 15);
                uint lastPage = 0;
                bool lastOk = false;
                for (uint i = 0; i < stop; i += 4)
                {
                    uint p = BitConverter.ToUInt32(buf, (int)i);
                    if (p < 0x10000 || (p & 3) != 0) continue;
                    uint d1 = BitConverter.ToUInt32(buf, (int)i + 4);
                    uint cnt = d1 & 0xFFFF, cap = d1 >> 16;
                    bool isArray = cnt >= 4 && cnt <= 2048 && cap >= cnt && cap <= 4096;
                    uint used = BitConverter.ToUInt32(buf, (int)i + 12);
                    bool isHash = d1 >= 4 && d1 <= 0x100000 && used >= 3 && used <= d1;
                    if (!isArray && !isHash) continue;
                    if ((p & ~0xFFFu) != lastPage) { lastPage = p & ~0xFFFu; lastOk = Mapped(p, 16); }
                    if (!lastOk) continue;
                    if (isArray) arrays.Add(new Candidate(reg.Base + off + i, p, d1));
                    if (isHash) hashes.Add(new Candidate(reg.Base + off + i, p, d1));
                }
            }
        }
        Log.Write("candidates: " + arrays.Count + " arrays, " + hashes.Count + " hash tables");

        int bestLive = 0;
        for (int i = 0; i < arrays.Count; i++)
        {
            int live = CountValidThreads(arrays[i].D0, arrays[i].D1 & 0xFFFF);
            if (live > bestLive)
            {
                bestLive = live;
                threadsArray = arrays[i].At;
            }
        }
        if (threadsArray == 0 || bestLive < 3) return "script thread list not found";
        Log.Write("thread list at 0x" + threadsArray.ToString("X8") + " (" + bestLive + " named threads)");

        List<ThreadInfo> threads = ReadThreads();
        for (int i = 0; i < hashes.Count && (programsTable == 0 || nativesTable == 0); i++)
        {
            Candidate c = hashes[i];
            if (programsTable == 0 && IsProgramTable(c.D0, c.D1, threads)) programsTable = c.At;
            else if (nativesTable == 0 && IsNativeTable(c.D0, c.D1)) nativesTable = c.At;
        }
        if (programsTable == 0) return "script program table not found";
        Log.Write("program table at 0x" + programsTable.ToString("X8"));
        if (nativesTable != 0)
            Log.Write("native table at 0x" + nativesTable.ToString("X8") + ", WAIT handler 0x" + Natives.WaitHandler.ToString("X8") +
                      ", TERMINATE 0x" + Natives.TerminateHandler.ToString("X8") + ", REGISTER_MISSION_PASSED 0x" + Natives.RegisterHandler.ToString("X8") +
                      ", PLAYSTATS_MISSION_PASSED 0x" + Natives.PlaystatsHandler.ToString("X8"));
        else
            Log.Write("native table not found, matching native hashes in code only");
        return null;
    }

    // Threads added by script hooks have their vtable outside the game executable and are
    // skipped; any pointer that cannot be read rejects the candidate.
    int CountValidThreads(uint data, uint count)
    {
        if (data < 0x10000 || count == 0 || !Mapped(data, count * 4)) return 0;
        byte[] ptrs = new byte[count * 4];
        if (!mem.Read(data, ptrs, ptrs.Length)) return 0;
        int seen = 0, game = 0;
        for (int i = 0; i < count && seen < 8; i++)
        {
            uint p = BitConverter.ToUInt32(ptrs, i * 4);
            if (p == 0) continue;
            seen++;
            uint vt;
            if (!Mapped(p, T_READ) || !TryU32(p, out vt)) return 0;
            if (InModule(vt)) game++;
        }
        if (seen == 0 || game * 2 < seen) return 0;
        int live = 0;
        game = 0;
        seen = 0;
        byte[] t = new byte[T_READ];
        for (int i = 0; i < count; i++)
        {
            uint p = BitConverter.ToUInt32(ptrs, i * 4);
            if (p == 0) continue;
            seen++;
            if (!Mapped(p, T_READ) || !mem.Read(p, t, T_READ)) return 0;
            if (InModule(BitConverter.ToUInt32(t, 0)) && BitConverter.ToUInt32(t, T_STATE) <= 4) game++;
            uint id = BitConverter.ToUInt32(t, T_ID);
            string name = CString(t, T_NAME, 24);
            if (id != 0 && name.Length > 0 && Joaat(name) == BitConverter.ToUInt32(t, T_HASH)) live++;
        }
        return game * 2 >= seen ? live : 0;
    }

    static string CString(byte[] b, int off, int max)
    {
        int n = 0;
        while (n < max && off + n < b.Length && b[off + n] != 0)
        {
            byte c = b[off + n];
            if (c < 0x20 || c > 0x7E) return "";
            n++;
        }
        if (n == max) return "";
        return Encoding.ASCII.GetString(b, off, n);
    }

    public List<ThreadInfo> ReadThreads()
    {
        List<ThreadInfo> list = new List<ThreadInfo>();
        uint data = U32(threadsArray);
        uint count = U32(threadsArray + 4) & 0xFFFF;
        if (data < 0x10000 || count == 0 || count > 4096) return list;
        byte[] ptrs = new byte[count * 4];
        if (!mem.Read(data, ptrs, ptrs.Length)) return list;
        byte[] t = new byte[T_READ];
        for (int i = 0; i < count; i++)
        {
            uint p = BitConverter.ToUInt32(ptrs, i * 4);
            if (p == 0 || !mem.Read(p, t, T_READ)) continue;
            if (!InModule(BitConverter.ToUInt32(t, 0))) continue;
            ThreadInfo ti = new ThreadInfo();
            ti.Address = p;
            ti.Id = BitConverter.ToUInt32(t, T_ID);
            ti.Hash = BitConverter.ToUInt32(t, T_HASH);
            ti.State = BitConverter.ToUInt32(t, T_STATE);
            ti.Name = CString(t, T_NAME, 24);
            ti.OnMission = t[T_ONMISSION] != 0;
            if (ti.Id == 0 || ti.State == S_KILLED || ti.Hash == 0) continue;
            if (ti.Name.Length == 0) ti.Name = "script " + ti.Hash.ToString("X8");
            list.Add(ti);
        }
        return list;
    }

    // Validation lookups give up after 32 probes; a real table finds its keys much sooner.
    bool IsProgramTable(uint entries, uint size, List<ThreadInfo> threads)
    {
        int ok = 0, tried = 0;
        for (int i = 0; i < threads.Count && tried < 12; i++)
        {
            if (tried == 3 && ok == 0) return false;
            tried++;
            uint prog;
            if (!HashLookupRaw(entries, size, threads[i].Hash, 32, out prog) || !Mapped(prog, 0x20)) continue;
            uint h, code, csize;
            if (!TryU32(prog + P_HASH, out h) || h != threads[i].Hash) continue;
            if (!TryU32(prog + P_CODE, out code) || !Mapped(code, 4)) continue;
            if (!TryU32(prog + P_CODESIZE, out csize) || csize < 4 || csize > 0x2000000) continue;
            byte[] b = new byte[1];
            if (!mem.Read(code, b, 1) || b[0] != ScoAnalysis.OP_ENTER) continue;
            ok++;
        }
        return ok >= 3 && ok * 4 >= tried * 3;
    }

    bool IsNativeTable(uint entries, uint size)
    {
        for (int set = 0; set < 2; set++)
        {
            uint w, t, r, s;
            if (!HashLookupRaw(entries, size, H_WAIT[set], 32, out w) || !InModule(w)) continue;
            if (!HashLookupRaw(entries, size, H_TERMINATE[set], 32, out t) || !InModule(t) || t == w) continue;
            if (!HashLookupRaw(entries, size, H_REGISTER_PASSED[set], 32, out r) || !InModule(r) || r == w || r == t) continue;
            if (!HashLookupRaw(entries, size, H_PLAYSTATS_PASSED[set], 32, out s) || !InModule(s)) s = 0;
            Natives.WaitHandler = w;
            Natives.TerminateHandler = t;
            Natives.RegisterHandler = r;
            Natives.PlaystatsHandler = s;
            Log.Write("native hash set: " + (set == 0 ? "1.0.7.0+ / EFLC / CE" : "1.0.6.0 and older"));
            return true;
        }
        return false;
    }

    bool ProgramCode(uint hash, out uint code, out byte[] bytes)
    {
        code = 0;
        bytes = null;
        uint prog, size;
        if (!HashLookup(programsTable, hash, out prog) || prog < 0x10000) return false;
        if (U32(prog + P_HASH) != hash) return false;
        code = U32(prog + P_CODE);
        size = U32(prog + P_CODESIZE);
        if (code < 0x10000 || size < 4 || size > 0x2000000) return false;
        bytes = new byte[size];
        return mem.Read(code, bytes, (int)size);
    }

    ScoAnalysis Analyze(uint hash, uint codeAddr, byte[] bytes)
    {
        string key = hash.ToString("X8") + ":" + codeAddr.ToString("X8") + ":" + bytes.Length + ":" + ScoAnalysis.Checksum(bytes);
        ScoAnalysis a;
        if (analysisCache.TryGetValue(key, out a)) return a;
        a = new ScoAnalysis(bytes, Natives);
        analysisCache[key] = a;
        return a;
    }

    public MissionTarget FindMission(out string reason)
    {
        List<ThreadInfo> threads = ReadThreads();
        List<MissionTarget> found = new List<MissionTarget>();
        List<ThreadInfo> owners = new List<ThreadInfo>();
        string lastWhy = "no running script has a mission pass";
        for (int i = 0; i < threads.Count; i++)
        {
            ThreadInfo ti = threads[i];
            uint codeAddr;
            byte[] bytes;
            if (!ProgramCode(ti.Hash, out codeAddr, out bytes)) continue;
            if (!ScoAnalysis.MayHaveAnchor(bytes, Natives)) continue;
            ScoAnalysis a = Analyze(ti.Hash, codeAddr, bytes);
            Log.Write("script " + ti.Name + " id " + ti.Id + (ti.OnMission ? " on-mission" : "") + ": " + a.Summary);
            if (a.Error != null) { lastWhy = ti.Name + ": " + a.Error; continue; }
            if (a.Pass == null) { lastWhy = ti.Name + " has no pass function (" + a.PickNote + ")"; continue; }
            MissionTarget m = new MissionTarget();
            m.Thread = ti.Address;
            m.Id = ti.Id;
            m.Hash = ti.Hash;
            m.Name = ti.Name;
            m.CodeAddress = codeAddr;
            m.Code = a;
            m.Pass = a.Pass;
            found.Add(m);
            owners.Add(ti);
        }
        reason = lastWhy;
        if (found.Count == 0) return null;
        int pick = 0;
        for (int i = 1; i < found.Count; i++)
        {
            bool bm = owners[pick].OnMission, im = owners[i].OnMission;
            if (im && !bm) pick = i;
            else if (im == bm && owners[i].Id > owners[pick].Id) pick = i;
        }
        if (found.Count > 1)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < found.Count; i++) sb.Append(found[i].Name).Append(owners[i].OnMission ? "*" : "").Append(' ');
            Log.Write("several scripts can pass: " + sb + "-> " + found[pick].Name);
        }
        reason = null;
        return found[pick];
    }

    public bool ThreadIsSame(MissionTarget t)
    {
        byte[] b = new byte[0x10];
        if (!mem.Read(t.Thread, b, b.Length)) return false;
        return BitConverter.ToUInt32(b, T_ID) == t.Id && BitConverter.ToUInt32(b, T_HASH) == t.Hash && BitConverter.ToUInt32(b, T_STATE) != S_KILLED;
    }

    // Returns null when the call was injected. A reason starting with '!' means give up,
    // any other reason means try again on a later frame.
    public string TryInject(MissionTarget t, bool mainOnly)
    {
        byte[] th = new byte[T_READ];
        if (!mem.Read(t.Thread, th, th.Length)) return "!mission thread unreadable";
        if (BitConverter.ToUInt32(th, T_ID) != t.Id || BitConverter.ToUInt32(th, T_HASH) != t.Hash) return "!mission thread changed";
        uint state = BitConverter.ToUInt32(th, T_STATE);
        uint pc = BitConverter.ToUInt32(th, T_PC);
        uint fp = BitConverter.ToUInt32(th, T_FP);
        uint sp = BitConverter.ToUInt32(th, T_SP);
        uint stackSize = BitConverter.ToUInt32(th, T_STACKSIZE);
        uint stack = BitConverter.ToUInt32(th, T_STACK);
        if (state != S_RUNNING && state != S_WAITING) return "thread state " + state;
        ScoAnalysis a = t.Code;
        if (!a.IsParkedAt(pc)) return "mission thread is not parked on a WAIT";
        if (stack < 0x10000 || sp == 0 || sp >= stackSize || fp >= sp || stackSize > 0x100000) return "!unexpected stack (sp " + sp + ", fp " + fp + ", size " + stackSize + ")";
        byte[] st = new byte[(sp + 1) * 4];
        if (!mem.Read(stack, st, st.Length)) return "!stack unreadable";

        List<ScoFunction> frames;
        string walk = a.WalkCallStack(pc, fp, sp, st, out frames);
        if (walk != null) return "!call stack check failed: " + walk;
        // A pass that is already running must not be started a second time. A frame in a
        // function that ends the script without passing (fail, cleanup) is waited out: if the
        // mission is really ending its thread is gone in a moment.
        for (int i = 0; i + 1 < frames.Count; i++)
        {
            ScoFunction f = frames[i];
            if (f.IsPassPath) return "!the mission is already passing";
        }
        for (int i = 0; i + 1 < frames.Count; i++)
        {
            ScoFunction f = frames[i];
            if (f.HasTerminate || (f.NoReturn && !f.ReachesAnchor)) return "mission is in sub_" + f.Entry.ToString("X") + ", which ends the script (fail or cleanup?)";
        }
        bool inMain = frames.Count == 1;
        if (mainOnly && !inMain) return "mission busy in sub_" + frames[0].Entry.ToString("X") + ", waiting for main loop";
        if ((ulong)sp + 1 + (ulong)t.Pass.StackNeed > stackSize) return "not enough script stack here (" + (stackSize - sp) + " free, " + t.Pass.StackNeed + " needed)";
        byte[] check = new byte[a.Code.Length];
        if (!mem.Read(t.CodeAddress, check, check.Length) || ScoAnalysis.Checksum(check) != a.CodeChecksum) return "!mission code changed";

        // CALL pass: push the return PC (this WAIT), then jump. The thread is not running
        // while a ScriptHookDotNet script ticks, so these writes cannot race the VM.
        uint oldWait = BitConverter.ToUInt32(th, T_WAIT);
        if (!mem.Write(stack + sp * 4, BitConverter.GetBytes(pc))) return "!stack write failed";
        bool ok = mem.Write(t.Thread + T_SP, BitConverter.GetBytes(sp + 1))
               && mem.Write(t.Thread + T_WAIT, BitConverter.GetBytes(0u))
               && mem.Write(t.Thread + T_PC, BitConverter.GetBytes(t.Pass.Entry))
               && mem.Write(t.Thread + T_STATE, BitConverter.GetBytes(S_RUNNING));
        byte[] after = new byte[T_READ];
        if (!ok || !mem.Read(t.Thread, after, after.Length)
            || BitConverter.ToUInt32(after, T_PC) != t.Pass.Entry || BitConverter.ToUInt32(after, T_SP) != sp + 1)
        {
            mem.Write(t.Thread + T_PC, BitConverter.GetBytes(pc));
            mem.Write(t.Thread + T_SP, BitConverter.GetBytes(sp));
            mem.Write(t.Thread + T_WAIT, BitConverter.GetBytes(oldWait));
            mem.Write(t.Thread + T_STATE, BitConverter.GetBytes(state));
            return "!thread write failed";
        }
        StringBuilder cs = new StringBuilder();
        for (int i = frames.Count - 1; i >= 0; i--) cs.Append(" sub_").Append(frames[i].Entry.ToString("X"));
        Log.Write("injected CALL sub_" + t.Pass.Entry.ToString("X") + " into " + t.Name + " at WAIT pc 0x" + pc.ToString("X") +
                  ", sp " + sp + "->" + (sp + 1) + ", fp " + fp + ", state " + state + ", stack size " + stackSize + ", call stack:" + cs);
        return null;
    }
}

class NativeIds
{
    public uint WaitHandler, TerminateHandler, RegisterHandler, PlaystatsHandler;

    static bool Is(uint v, uint handler, uint[] hashes)
    {
        return (handler != 0 && v == handler) || v == hashes[0] || v == hashes[1];
    }
    public bool IsWait(uint v) { return Is(v, WaitHandler, ScriptEngine.H_WAIT); }
    public bool IsTerminate(uint v) { return Is(v, TerminateHandler, ScriptEngine.H_TERMINATE); }
    public bool IsRegisterPassed(uint v) { return Is(v, RegisterHandler, ScriptEngine.H_REGISTER_PASSED); }
    public bool IsPlaystatsPassed(uint v) { return Is(v, PlaystatsHandler, ScriptEngine.H_PLAYSTATS_PASSED); }
}

// ------------------------------------------------------------------ SCO code analysis

class ScoFunction
{
    public uint Entry;
    public uint End;
    public int ArgCount;
    public int FrameSize;
    public List<uint> Insns = new List<uint>();
    public List<uint> Callees = new List<uint>();
    public int CallSites;
    public bool CalledByMain;
    public bool Bad;
    public bool HasAnchor;
    public bool HasTerminate;
    public bool ReachesAnchor;
    public bool NoReturn;
    public bool Unconditional;
    public bool IsPassPath;
    public bool WritesState;
    public int StackNeed;
}

class ScoAnalysis
{
    public const byte OP_J = 34, OP_JZ = 35, OP_JNZ = 36, OP_PUSH_S16 = 40, OP_PUSH_U32 = 41, OP_PUSH_F = 42;
    public const byte OP_NATIVE = 45, OP_CALL = 46, OP_ENTER = 47, OP_LEAVE = 48, OP_SWITCH = 66, OP_STRING = 67;
    public const byte OP_STORE = 50, OP_STATIC = 63, OP_GLOBAL = 64;
    public const byte OP_TL_FIRST = 69, OP_TL_LAST = 72, OP_THROW = 74, OP_EXIT = 79;

    public byte[] Code;
    public uint CodeChecksum;
    public string Error;
    public string PickNote = "";
    public string Summary = "";
    public ScoFunction Pass;
    public List<ScoFunction> Functions = new List<ScoFunction>();
    Dictionary<uint, ScoFunction> byEntry = new Dictionary<uint, ScoFunction>();
    bool[] isStart;
    NativeIds natives;
    bool anchorIsRegister;

    public static uint Checksum(byte[] b)
    {
        uint h = 2166136261;
        for (int i = 0; i < b.Length; i++) { h ^= b[i]; h *= 16777619; }
        return h;
    }

    public static int InsnSize(byte[] code, uint pc)
    {
        byte op = code[pc];
        switch (op)
        {
            case OP_J: case OP_JZ: case OP_JNZ: case OP_PUSH_U32: case OP_PUSH_F: case OP_CALL: return 5;
            case OP_PUSH_S16: return 3;
            case OP_NATIVE: return 7;
            case OP_ENTER: return 4;
            case OP_LEAVE: return 3;
            case OP_STRING: return pc + 1 < code.Length ? 2 + code[pc + 1] : -1;
            case OP_SWITCH: return pc + 1 < code.Length ? 2 + code[pc + 1] * 8 : -1;
        }
        if (op >= OP_TL_FIRST && op <= OP_TL_LAST) return 2;
        return 1;
    }

    static uint U32(byte[] b, uint at) { return BitConverter.ToUInt32(b, (int)at); }

    // Cheap pre-filter so only scripts that can pass a mission are disassembled.
    public static bool MayHaveAnchor(byte[] code, NativeIds n)
    {
        for (int i = 0; i + 7 <= code.Length; i++)
        {
            if (code[i] != OP_NATIVE || code[i + 1] != 1 || code[i + 2] != 0) continue;
            uint v = BitConverter.ToUInt32(code, i + 3);
            if (n.IsRegisterPassed(v) || n.IsPlaystatsPassed(v)) return true;
        }
        return false;
    }

    public ScoAnalysis(byte[] code, NativeIds n)
    {
        Code = code;
        CodeChecksum = Checksum(code);
        natives = n;
        try
        {
            Error = Build();
        }
        catch (Exception ex)
        {
            Error = "analysis failed: " + ex.Message;
        }
        if (Error != null) Summary = "not analysable: " + Error;
    }

    bool IsAnchorNative(uint v) { return anchorIsRegister ? natives.IsRegisterPassed(v) : natives.IsPlaystatsPassed(v); }

    public bool IsWaitAt(uint pc)
    {
        return (ulong)pc + 7 <= (ulong)Code.Length && isStart[pc] && Code[pc] == OP_NATIVE && Code[pc + 1] == 1 && Code[pc + 2] == 0 && natives.IsWait(U32(Code, pc + 3));
    }

    // A thread blocked in WAIT has its PC on the WAIT (scrDbg's VM); a PC right after a WAIT
    // is accepted as well. Either way it is the statement boundary the call returns to.
    public bool IsParkedAt(uint pc)
    {
        if (IsWaitAt(pc)) return true;
        return pc >= 7 && pc < Code.Length && isStart[pc] && IsWaitAt(pc - 7);
    }

    ScoFunction FunctionAt(uint pc)
    {
        int lo = 0, hi = Functions.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            ScoFunction f = Functions[mid];
            if (pc < f.Entry) hi = mid - 1;
            else if (pc >= f.End) lo = mid + 1;
            else return f;
        }
        return null;
    }

    string Build()
    {
        uint n = (uint)Code.Length;
        isStart = new bool[n + 1];
        if (n < 4 || Code[0] != OP_ENTER) return "code does not start with ENTER";
        List<uint> all = new List<uint>();
        uint pc = 0;
        while (pc < n)
        {
            int len = InsnSize(Code, pc);
            if (len <= 0 || pc + (uint)len > n) return "instruction overruns code at 0x" + pc.ToString("X");
            isStart[pc] = true;
            all.Add(pc);
            pc += (uint)len;
        }

        bool sawRegister = false, sawPlaystats = false;
        ScoFunction cur = null;
        for (int i = 0; i < all.Count; i++)
        {
            uint at = all[i];
            byte op = Code[at];
            if (op == OP_ENTER)
            {
                if (cur != null) cur.End = at;
                cur = new ScoFunction();
                cur.Entry = at;
                cur.ArgCount = Code[at + 1];
                cur.FrameSize = BitConverter.ToUInt16(Code, (int)at + 2);
                Functions.Add(cur);
                byEntry[at] = cur;
            }
            cur.Insns.Add(at);
            if (op == OP_NATIVE)
            {
                uint v = U32(Code, at + 3);
                if (natives.IsRegisterPassed(v)) sawRegister = true;
                if (natives.IsPlaystatsPassed(v)) sawPlaystats = true;
            }
        }
        cur.End = n;
        anchorIsRegister = sawRegister;
        if (!sawRegister && !sawPlaystats) return "no REGISTER_MISSION_PASSED or PLAYSTATS_MISSION_PASSED";

        ScoFunction main = Functions[0];
        for (int f = 0; f < Functions.Count; f++)
        {
            ScoFunction fn = Functions[f];
            for (int k = 0; k < fn.Insns.Count; k++)
            {
                uint at = fn.Insns[k];
                byte op = Code[at];
                if (op == OP_J || op == OP_JZ || op == OP_JNZ)
                {
                    uint t = U32(Code, at + 1);
                    if (t >= n || !isStart[t] || t < fn.Entry || t >= fn.End) fn.Bad = true;
                }
                else if (op == OP_SWITCH)
                {
                    int cnt = Code[at + 1];
                    for (int c = 0; c < cnt; c++)
                    {
                        uint t = U32(Code, at + 2 + (uint)c * 8 + 4);
                        if (t >= n || !isStart[t] || t < fn.Entry || t >= fn.End) fn.Bad = true;
                    }
                }
                else if (op == OP_CALL)
                {
                    uint t = U32(Code, at + 1);
                    ScoFunction g;
                    if (t >= n || !byEntry.TryGetValue(t, out g)) return "CALL to a non-function at 0x" + at.ToString("X");
                    if (!fn.Callees.Contains(t)) fn.Callees.Add(t);
                    g.CallSites++;
                    if (fn == main) g.CalledByMain = true;
                }
                else if (op == OP_NATIVE)
                {
                    uint v = U32(Code, at + 3);
                    if (IsAnchorNative(v)) fn.HasAnchor = true;
                    if (natives.IsTerminate(v)) fn.HasTerminate = true;
                }
                else if (op == OP_ENTER && k != 0)
                {
                    fn.Bad = true;
                }
                else if (op == OP_STORE && k >= 1)
                {
                    // static or global written directly by this function (stage, loop flag, flow)
                    for (int b = Math.Max(0, k - 4); b < k; b++)
                    {
                        byte prev = Code[fn.Insns[b]];
                        if (prev == OP_STATIC || prev == OP_GLOBAL) fn.WritesState = true;
                    }
                }
            }
        }

        // reaches the anchor through calls
        for (int f = 0; f < Functions.Count; f++) Functions[f].ReachesAnchor = Functions[f].HasAnchor;
        for (bool changed = true; changed; )
        {
            changed = false;
            for (int f = 0; f < Functions.Count; f++)
            {
                ScoFunction fn = Functions[f];
                if (fn.ReachesAnchor) continue;
                for (int c = 0; c < fn.Callees.Count; c++)
                    if (byEntry[fn.Callees[c]].ReachesAnchor) { fn.ReachesAnchor = true; changed = true; break; }
            }
        }

        // never returns: no LEAVE / THROW reachable without passing TERMINATE_THIS_SCRIPT,
        // EXIT or a call to a function that never returns
        for (bool changed = true; changed; )
        {
            changed = false;
            for (int f = 0; f < Functions.Count; f++)
            {
                ScoFunction fn = Functions[f];
                if (fn.NoReturn || fn.Bad) continue;
                if (!CanReturn(fn)) { fn.NoReturn = true; changed = true; }
            }
        }

        // always reaches the anchor; grows until stable because a call into a function that
        // already always reaches it counts as reaching it
        for (bool changed = true; changed; )
        {
            changed = false;
            for (int f = 1; f < Functions.Count; f++)
            {
                ScoFunction fn = Functions[f];
                if (fn.Unconditional || fn.Bad || !fn.ReachesAnchor) continue;
                if (AlwaysReachesAnchor(fn)) { fn.Unconditional = true; changed = true; }
            }
        }
        for (int f = 0; f < Functions.Count; f++)
            Functions[f].IsPassPath = Functions[f].Unconditional || Functions[f].HasAnchor;

        PickPass(main);
        if (Pass != null) Pass.StackNeed = StackNeed(Pass, new Dictionary<uint, int>(), 0);

        int nr = 0, unc = 0;
        for (int f = 0; f < Functions.Count; f++) { if (Functions[f].NoReturn) nr++; if (Functions[f].Unconditional && f > 0) unc++; }
        Summary = Functions.Count + " functions, anchor " + (anchorIsRegister ? "REGISTER_MISSION_PASSED" : "PLAYSTATS_MISSION_PASSED") +
                  ", " + unc + " pass candidates, " + nr + " never return, " + PickNote;
        return null;
    }

    void Successors(ScoFunction fn, uint at, List<uint> outList, out bool returns, out bool terminal)
    {
        outList.Clear();
        returns = false;
        terminal = false;
        byte op = Code[at];
        uint next = at + (uint)InsnSize(Code, at);
        switch (op)
        {
            case OP_J:
                outList.Add(U32(Code, at + 1));
                return;
            case OP_JZ:
            case OP_JNZ:
                outList.Add(U32(Code, at + 1));
                break;
            case OP_SWITCH:
                {
                    int cnt = Code[at + 1];
                    for (int c = 0; c < cnt; c++) outList.Add(U32(Code, at + 2 + (uint)c * 8 + 4));
                    break;
                }
            case OP_LEAVE:
            case OP_THROW:
                returns = true;
                return;
            case OP_EXIT:
                terminal = true;
                return;
            case OP_NATIVE:
                if (natives.IsTerminate(U32(Code, at + 3))) { terminal = true; return; }
                break;
            case OP_CALL:
                if (byEntry[U32(Code, at + 1)].NoReturn) { terminal = true; return; }
                break;
        }
        if (next >= fn.End) { returns = true; return; }
        outList.Add(next);
    }

    bool CanReturn(ScoFunction fn)
    {
        if (fn.Insns.Count < 2) return true;
        Dictionary<uint, bool> seen = new Dictionary<uint, bool>();
        Stack<uint> work = new Stack<uint>();
        List<uint> succ = new List<uint>();
        work.Push(fn.Insns[1]);
        while (work.Count > 0)
        {
            uint at = work.Pop();
            if (seen.ContainsKey(at)) continue;
            seen[at] = true;
            bool ret, term;
            Successors(fn, at, succ, out ret, out term);
            if (ret) return true;
            for (int i = 0; i < succ.Count; i++) work.Push(succ[i]);
        }
        return false;
    }

    // A call counts as reaching the anchor when the callee contains it, always reaches it, or
    // takes parameters and reaches it: the shared mission-passed stat routines take
    // parameters and may return early inside, while the stage and per-frame functions that
    // only call the pass function under a condition take none.
    bool CallReachesAnchor(ScoFunction g)
    {
        return g.HasAnchor || g.Unconditional || (g.ReachesAnchor && g.ArgCount > 0);
    }

    // Every path from the entry reaches the anchor before any exit.
    bool AlwaysReachesAnchor(ScoFunction fn)
    {
        if (fn.Insns.Count < 2) return false;
        Dictionary<uint, bool> seen = new Dictionary<uint, bool>();
        Stack<uint> work = new Stack<uint>();
        List<uint> succ = new List<uint>();
        bool any = false;
        work.Push(fn.Insns[1]);
        while (work.Count > 0)
        {
            uint at = work.Pop();
            if (seen.ContainsKey(at)) continue;
            seen[at] = true;
            byte op = Code[at];
            if (op == OP_NATIVE && IsAnchorNative(U32(Code, at + 3))) { any = true; continue; }
            if (op == OP_CALL && CallReachesAnchor(byEntry[U32(Code, at + 1)])) { any = true; continue; }
            bool ret, term;
            Successors(fn, at, succ, out ret, out term);
            if (ret || term) return false;
            for (int i = 0; i < succ.Count; i++) work.Push(succ[i]);
        }
        return any;
    }

    void PickPass(ScoFunction main)
    {
        List<ScoFunction> cands = new List<ScoFunction>();
        for (int f = 1; f < Functions.Count; f++)
            if (Functions[f].Unconditional && Functions[f].CallSites > 0) cands.Add(Functions[f]);
        if (cands.Count == 0) { PickNote = "no unconditional pass function"; return; }

        List<ScoFunction> picks = new List<ScoFunction>();
        List<ScoFunction> endsScript = new List<ScoFunction>();
        for (int i = 0; i < cands.Count; i++) if (cands[i].NoReturn) endsScript.Add(cands[i]);
        if (endsScript.Count > 0)
        {
            for (int i = 0; i < endsScript.Count; i++)
            {
                bool callsInner = false;
                for (int j = 0; j < endsScript.Count && !callsInner; j++)
                    if (j != i && endsScript[i].Callees.Contains(endsScript[j].Entry)) callsInner = true;
                if (!callsInner) picks.Add(endsScript[i]);
            }
        }
        else
        {
            List<ScoFunction> top = new List<ScoFunction>();
            for (int i = 0; i < cands.Count; i++)
            {
                bool hasCandCaller = false;
                for (int j = 0; j < cands.Count && !hasCandCaller; j++)
                    if (j != i && cands[j].Callees.Contains(cands[i].Entry)) hasCandCaller = true;
                if (!hasCandCaller) top.Add(cands[i]);
            }
            // A pass that returns only ends the mission when it changes the mission's own state
            // (exit flag, final stage). Where it does not, the rest of the pass sequence is
            // written inline in the caller; calling it alone would only register the stats.
            for (int i = 0; i < top.Count; i++) if (top[i].CalledByMain && top[i].WritesState) picks.Add(top[i]);
            if (picks.Count == 0)
                for (int i = 0; i < top.Count; i++) if (top[i].WritesState) picks.Add(top[i]);
            if (picks.Count == 0)
            {
                PickNote = "pass sequence is inline in the mission's main loop, not a separate function";
                return;
            }
        }
        if (picks.Count == 0) { PickNote = "pass candidates call each other"; return; }

        ScoFunction best = picks[0];
        for (int i = 1; i < picks.Count; i++)
        {
            ScoFunction p = picks[i];
            if (p.CalledByMain != best.CalledByMain) { if (p.CalledByMain) best = p; continue; }
            if (p.CallSites > best.CallSites) best = p;
        }
        Pass = best;
        StringBuilder sb = new StringBuilder("pass sub_" + best.Entry.ToString("X") + (best.NoReturn ? " ends script" : " returns"));
        if (picks.Count > 1)
        {
            sb.Append(", also");
            for (int i = 0; i < picks.Count; i++) if (picks[i] != best) sb.Append(" sub_").Append(picks[i].Entry.ToString("X"));
        }
        PickNote = sb.ToString();
    }

    // Upper bound for the stack slots the pass needs above the current SP: per frame the
    // return PC, caller FP, the ENTER frame, and room for expression values.
    int StackNeed(ScoFunction fn, Dictionary<uint, int> memo, int depth)
    {
        int v;
        if (memo.TryGetValue(fn.Entry, out v)) return v;
        if (depth > 48) return 4096;
        memo[fn.Entry] = 4096;
        int worst = 0;
        for (int i = 0; i < fn.Callees.Count; i++)
            worst = Math.Max(worst, StackNeed(byEntry[fn.Callees[i]], memo, depth + 1));
        v = 2 + fn.FrameSize + 32 + worst;
        if (v > 4096) v = 4096;
        memo[fn.Entry] = v;
        return v;
    }

    // Walks the frames exactly like LEAVE would unwind them. Returns null when every frame
    // is consistent, frames[0] being the function the thread is in now and the last one main.
    public string WalkCallStack(uint pc, uint fp, uint sp, byte[] stack, out List<ScoFunction> frames)
    {
        frames = new List<ScoFunction>();
        for (int depth = 0; depth < 64; depth++)
        {
            ScoFunction f = FunctionAt(pc);
            if (f == null) return "pc 0x" + pc.ToString("X") + " outside code";
            frames.Add(f);
            ulong slot = (ulong)fp + (ulong)f.ArgCount;
            if (slot + 1 >= sp) return "frame of sub_" + f.Entry.ToString("X") + " above sp";
            uint retPc = BitConverter.ToUInt32(stack, (int)slot * 4);
            uint callerFp = BitConverter.ToUInt32(stack, (int)(slot + 1) * 4);
            if (retPc == 0)
            {
                if (f.Entry != 0) return "chain ends in sub_" + f.Entry.ToString("X") + ", not main";
                return null;
            }
            if (retPc < 5 || retPc > Code.Length) return "return pc 0x" + retPc.ToString("X") + " outside code";
            uint call = retPc - 5;
            if (!isStart[call] || Code[call] != OP_CALL || U32(Code, call + 1) != f.Entry)
                return "return pc 0x" + retPc.ToString("X") + " does not follow a CALL to sub_" + f.Entry.ToString("X");
            if (callerFp >= fp) return "caller fp " + callerFp + " not below fp " + fp;
            pc = call;
            fp = callerFp;
        }
        return "call stack deeper than 64";
    }
}
