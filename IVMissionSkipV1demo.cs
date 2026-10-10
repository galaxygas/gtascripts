// MissionSkipF9.cs
// F9 passes the running GTA IV / EFLC mission by sending that mission's own
// thread through its own REGISTER_MISSION_PASSED function.
//
// This is the SCO equivalent of the VCDE skip: the mission's pass block runs,
// so the rewards, globals, phone, and cleanup are the ones the mission wrote.
// The thread is not terminated from outside.
//
// Host: ScriptHookDotNet (.cs compiled at load). Layout of the mission thread
// is Aru's ScriptHook GameTypes.h (GtaThread). Native hash is the IV-Network
// native table. Opcode sizes are the SCO table on grandtheftwiki.com/SCO.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using GTA;

public class MissionSkipF9 : Script
{
    const int KEY = 120; // F9, also bound below

    // Aru, ScriptHook/GameTypes.h. Offsets from the GtaThread object.
    const int OFF_STATE = 0x0C;
    const int OFF_IP = 0x10;
    const int OFF_WAIT = 0x28;
    const int OFF_STACK = 0x58;
    const int OFF_NAME = 0x70;
    const int NAME_LEN = 24;
    const int STATE_RUNNING = 1;

    // opcode 45, documented 7 bytes: op, argc, retc, hash.
    // REGISTER_MISSION_PASSED = 0x5FBE5F52 (IV-Network ScriptFunctions.h).
    const byte OP_NATIVE = 0x2D;
    const byte OP_CALL = 0x2E;
    const byte OP_ENTER = 0x2F;
    const uint HASH_REGISTER_MISSION_PASSED = 0x5FBE5F52;

    static readonly string[] MissionNames = new string[]
    {
        "badman_1", "bell2", "bell3m", "bell3p", "bell5", "bell6", "bell7",
        "bernie1", "bernie2", "bernie3", "brian_1", "brian_2", "brian_3",
        "brucie1", "brucie2", "brucie3_meetguy", "brucie3a", "brucie3b", "brucie4", "brucie5m", "brucie5p",
        "cherise", "cia1", "cia2", "cia3", "cia4", "clarence",
        "derrick1", "derrick2", "derrick3", "dwayne1", "dwayne3",
        "eddie1", "eddie2", "elizabeta1", "elizabeta2", "elizabeta3", "elizabeta4",
        "faustin1", "faustin2", "faustin3", "faustin4", "faustin5", "faustin5_fire", "faustin6", "faustin7",
        "finale1a", "finale1b", "finale1c", "finale1d", "finale2", "finaleaorb", "finaleintrocut",
        "francis1", "francis2_appointment", "francis2a", "francis2b", "francis3", "francis4", "francis5", "francis6",
        "gambetti1", "gambetti2", "gambetti3",
        "gerry1", "gerry2", "gerry3_phonegirl", "gerry3a", "gerry3b", "gerry3c", "gerry4", "gerry5",
        "gracie", "hossan_1", "ilyena", "ivan_1", "jacob1", "jacob2", "jacob3m", "jacob3p",
        "jeff_1", "jeff_1_mission", "jeff_2", "jeff_3",
        "jimmy1", "jimmy2", "jimmy3", "jimmy4", "jimmy4a",
        "manny1", "manny2", "manny3", "marnie1", "marnie2", "michelle1",
        "packie1", "packie2", "packie3", "packie_bomb", "playboy2", "playboy3", "playboy4",
        "roman1", "roman2", "roman3", "roman4", "roman5", "roman6", "roman7",
        "roman8m", "roman8p", "roman9", "roman10m", "roman10p", "roman11", "roman12", "roman13", "roman14",
        "vlad1", "vlad2", "vlad3", "vlad4",
        "ashley1", "ashley2", "billy1", "billy2", "billy3", "billy4", "billy5", "billy6",
        "brian", "dave", "gangwar1", "gangwar2", "gangwarc",
        "jim1", "jim2", "jim3m", "jim3p", "jim4", "jim5", "jim6",
        "malc1", "malc2", "malcrace", "ray1", "ray2", "ray3",
        "stubbs1", "stubbs2", "stubbs3m1", "stubbs3m2", "stubbs3m4", "stubbs3m5", "stubbs3m7", "stubbs3p", "stubbs4",
        "arnaud1", "arnaud2", "brother1", "brother2", "brother3",
        "bulgarin1", "bulgarin2", "bulgarin3", "daisy1", "friends1", "friends2",
        "margot1", "margot2", "mum1", "rocco1",
        "tony1", "tony2", "tony3", "tony4a", "tony4b", "tony5", "tony6", "tony7", "tony8", "tony9", "tony10", "tony11",
        "tonym1", "tonym2", "tonym3", "tonym4", "tonym5", "tonym6", "tonym7", "tonym8",
        "yusuf1", "yusuf2", "yusuf3", "yusuf4",
        "drugwarsconvoy", "drugwarsdeal", "drugwarspickup", "drugwarsshipment"
    };

    int lastPress;
    IntPtr armedThread;
    int armedIp;
    int armedAt;
    string armedName;

    public MissionSkipF9()
    {
        BindKey(Keys.F9, new KeyPressDelegate(OnSkip));
        Interval = 250;
        this.Tick += new EventHandler(this.OnTick);
        Say("Mission pass loaded. F9 runs the mission's own pass block.");
        Log("loaded");
    }

    private void OnTick(object sender, EventArgs e)
    {
        if (armedThread == IntPtr.Zero) return;
        if (Game.GameTime - armedAt > 8000)
        {
            Log("gave up waiting for " + armedName);
            armedThread = IntPtr.Zero;
            return;
        }
        bool on = GTA.Native.Function.Call<bool>("GET_MISSION_FLAG");
        int ip = ReadI32(armedThread + OFF_IP);
        if (!on)
        {
            Say("Mission passed: " + armedName);
            Log("mission flag cleared after redirect, ip now " + ip);
            armedThread = IntPtr.Zero;
            return;
        }
        // One retry if the VM was inside a native and restored the old IP.
        if (ip != armedIp && Game.GameTime - armedAt > 1000 && Game.GameTime - armedAt < 1500)
        {
            WriteI32(armedThread + OFF_IP, armedIp);
            WriteF32(armedThread + OFF_WAIT, 0f);
            Log("rearmed ip " + armedIp + " (was " + ip + ")");
        }
    }

    private void OnSkip()
    {
        int now = Game.GameTime;
        if (now - lastPress < 1000) return;
        lastPress = now;
        if (armedThread != IntPtr.Zero)
        {
            Say("Pass already armed");
            return;
        }
        if (!GTA.Native.Function.Call<bool>("GET_MISSION_FLAG"))
        {
            Say("Not on a mission");
            return;
        }

        List<IntPtr> threads = FindMissionThreads();
        if (threads.Count == 0)
        {
            Say("Mission thread not found. See scripts\\MissionSkipF9.log");
            Log("no GtaThread with a known mission name while mission flag is set");
            return;
        }

        IntPtr thread = threads[0];
        string name = ReadName(thread);
        int ip = ReadI32(thread + OFF_IP);
        Log("thread " + name + " at " + thread.ToString("X8") + " ip " + ip + " state " + ReadI32(thread + OFF_STATE));

        List<CodeImage> images = FindPassImages();
        if (images.Count == 0)
        {
            Say("No REGISTER_MISSION_PASSED in loaded code");
            Log("native pattern 2D ?? ?? 52 5F BE 5F not found");
            return;
        }

        CodeImage image = PickImage(images, ip);
        if (image == null)
        {
            Say("Pass block is not in this mission's code");
            Log("found " + images.Count + " pass sites, none cover ip " + ip);
            return;
        }

        int callAt = FindCallToPass(image);
        if (callAt < 0)
        {
            Say("Pass block is not called. Refusing.");
            Log(name + " has REGISTER_MISSION_PASSED at +" + image.NativeAt + " enter +" + image.EnterAt + " but no call to that enter");
            return;
        }

        WriteI32(thread + OFF_IP, callAt);
        WriteF32(thread + OFF_WAIT, 0f);
        int after = ReadI32(thread + OFF_IP);
        if (after != callAt)
        {
            Say("Could not set the mission thread IP");
            Log("write ip failed, still " + after);
            return;
        }

        armedThread = thread;
        armedIp = callAt;
        armedAt = now;
        armedName = name;
        Say("Passing " + name + "...");
        Log("armed " + name + " ip " + ip + " -> call +" + callAt + " enter +" + image.EnterAt + " native +" + image.NativeAt);
    }

    private CodeImage PickImage(List<CodeImage> images, int ip)
    {
        CodeImage best = null;
        for (int i = 0; i < images.Count; i++)
        {
            CodeImage c = images[i];
            if (ip >= 0 && ip < c.Size) best = c;
        }
        if (best != null) return best;
        // IP may be an absolute address on a build where it is not an offset.
        for (int i = 0; i < images.Count; i++)
        {
            CodeImage c = images[i];
            long start = c.Base.ToInt64();
            if (ip >= start && ip < start + c.Size) return c;
        }
        return images.Count == 1 ? images[0] : null;
    }

    private int FindCallToPass(CodeImage image)
    {
        byte[] code = image.Code;
        int enter = image.EnterAt;
        for (int i = 0; i + 5 <= code.Length; i++)
        {
            if (code[i] != OP_CALL) continue;
            int target = BitConverter.ToInt32(code, i + 1);
            if (target == enter) return i;
        }
        return -1;
    }

    private List<CodeImage> FindPassImages()
    {
        List<CodeImage> found = new List<CodeImage>();
        byte[] needle = new byte[] { OP_NATIVE, 0x01, 0x00, 0x52, 0x5F, 0xBE, 0x5F };
        ForEachRegion(delegate(IntPtr baseAddr, int size)
        {
            if (size < 64 || size > 8 * 1024 * 1024) return;
            byte[] buf = ReadBytes(baseAddr, size);
            if (buf == null) return;
            int at = 0;
            while (at + 7 <= buf.Length)
            {
                int hit = IndexOf(buf, needle, at);
                if (hit < 0) break;
                int header = FindScoHeader(buf, hit);
                int codeStart = header >= 0 ? header + 24 : Math.Max(0, hit - 65536);
                int codeSize = header >= 0 ? BitConverter.ToInt32(buf, header + 4) : Math.Min(buf.Length - codeStart, 512 * 1024);
                if (codeSize < 16 || codeStart + codeSize > buf.Length) { at = hit + 1; continue; }
                int nativeAt = hit - codeStart;
                int enterAt = EnterFor(buf, codeStart, codeSize, nativeAt);
                if (enterAt >= 0)
                {
                    byte[] code = new byte[codeSize];
                    Buffer.BlockCopy(buf, codeStart, code, 0, codeSize);
                    CodeImage img = new CodeImage();
                    img.Base = new IntPtr(baseAddr.ToInt64() + codeStart);
                    img.Code = code;
                    img.Size = codeSize;
                    img.NativeAt = nativeAt;
                    img.EnterAt = enterAt;
                    found.Add(img);
                    Log("pass site code " + img.Base.ToString("X8") + " native +" + nativeAt + " enter +" + enterAt);
                }
                at = hit + 7;
            }
        });
        return found;
    }

    // Greatest enter opcode at or before the native, and a call somewhere targets it.
    private int EnterFor(byte[] buf, int codeStart, int codeSize, int nativeAt)
    {
        int enter = -1;
        int end = codeStart + codeSize;
        for (int i = codeStart; i + 4 <= end && i <= codeStart + nativeAt; i++)
        {
            if (buf[i] == OP_ENTER) enter = i - codeStart;
        }
        if (enter < 0) return -1;
        for (int i = codeStart; i + 5 <= end; i++)
        {
            if (buf[i] != OP_CALL) continue;
            int target = BitConverter.ToInt32(buf, i + 1);
            if (target == enter) return enter;
        }
        return -1;
    }

    private int FindScoHeader(byte[] buf, int hit)
    {
        int from = Math.Max(0, hit - 1024 * 1024);
        for (int i = hit - 24; i >= from; i--)
        {
            if (buf[i] == 0x53 && buf[i + 1] == 0x43 && buf[i + 2] == 0x52 && buf[i + 3] == 0x0D)
            {
                int codeSize = BitConverter.ToInt32(buf, i + 4);
                if (codeSize > 16 && codeSize < 2 * 1024 * 1024 && i + 24 + codeSize > hit) return i;
            }
        }
        return -1;
    }

    private List<IntPtr> FindMissionThreads()
    {
        List<IntPtr> hits = new List<IntPtr>();
        ForEachRegion(delegate(IntPtr baseAddr, int size)
        {
            if (size < NAME_LEN || size > 16 * 1024 * 1024) return;
            byte[] buf = ReadBytes(baseAddr, size);
            if (buf == null) return;
            for (int n = 0; n < MissionNames.Length; n++)
            {
                byte[] name = Encoding.ASCII.GetBytes(MissionNames[n]);
                int at = 0;
                while (at + name.Length + 1 <= buf.Length)
                {
                    int hit = IndexOf(buf, name, at);
                    if (hit < 0) break;
                    at = hit + 1;
                    if (hit + NAME_LEN > buf.Length) continue;
                    if (buf[hit + name.Length] != 0) continue;
                    if (hit < OFF_NAME) continue;
                    int obj = hit - OFF_NAME;
                    if (obj + OFF_STACK + 4 > buf.Length) continue;
                    int state = BitConverter.ToInt32(buf, obj + OFF_STATE);
                    int ip = BitConverter.ToInt32(buf, obj + OFF_IP);
                    int stack = BitConverter.ToInt32(buf, obj + OFF_STACK);
                    if (state != STATE_RUNNING) continue;
                    if (ip < 0 || ip > 2 * 1024 * 1024) continue;
                    if (stack == 0) continue;
                    IntPtr thread = new IntPtr(baseAddr.ToInt64() + obj);
                    hits.Add(thread);
                    Log("candidate " + MissionNames[n] + " " + thread.ToString("X8"));
                }
            }
        });
        return hits;
    }

    private delegate void RegionFn(IntPtr baseAddr, int size);

    private void ForEachRegion(RegionFn fn)
    {
        IntPtr addr = IntPtr.Zero;
        MEMORY_BASIC_INFORMATION info = new MEMORY_BASIC_INFORMATION();
        int mbiSize = Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION));
        while (VirtualQuery(addr, out info, (uint)mbiSize) == mbiSize)
        {
            long next = info.BaseAddress.ToInt64() + info.RegionSize.ToInt64();
            bool commit = info.State == 0x1000;
            bool guard = (info.Protect & 0x100) != 0;
            bool readable = (info.Protect & 0xEE) != 0;
            if (commit && !guard && readable && info.RegionSize.ToInt64() > 0 && info.RegionSize.ToInt64() < int.MaxValue)
            {
                try { fn(info.BaseAddress, (int)info.RegionSize); }
                catch (Exception ex) { Log("region " + info.BaseAddress.ToString("X8") + " " + ex.Message); }
            }
            if (next <= addr.ToInt64()) break;
            addr = new IntPtr(next);
        }
    }

    private string ReadName(IntPtr thread)
    {
        byte[] b = ReadBytes(thread + OFF_NAME, NAME_LEN);
        if (b == null) return "?";
        int n = 0;
        while (n < b.Length && b[n] != 0) n++;
        return Encoding.ASCII.GetString(b, 0, n);
    }

    private int ReadI32(IntPtr addr)
    {
        byte[] b = ReadBytes(addr, 4);
        if (b == null) return 0;
        return BitConverter.ToInt32(b, 0);
    }

    private void WriteI32(IntPtr addr, int value)
    {
        byte[] b = BitConverter.GetBytes(value);
        WriteBytes(addr, b);
    }

    private void WriteF32(IntPtr addr, float value)
    {
        byte[] b = BitConverter.GetBytes(value);
        WriteBytes(addr, b);
    }

    private byte[] ReadBytes(IntPtr addr, int size)
    {
        byte[] b = new byte[size];
        int read;
        if (!ReadProcessMemory(GetCurrentProcess(), addr, b, size, out read) || read != size) return null;
        return b;
    }

    private void WriteBytes(IntPtr addr, byte[] b)
    {
        int wrote;
        WriteProcessMemory(GetCurrentProcess(), addr, b, b.Length, out wrote);
    }

    private int IndexOf(byte[] hay, byte[] needle, int from)
    {
        for (int i = from; i + needle.Length <= hay.Length; i++)
        {
            int k = 0;
            for (; k < needle.Length; k++) if (hay[i + k] != needle[k]) break;
            if (k == needle.Length) return i;
        }
        return -1;
    }

    private void Say(string msg)
    {
        Game.DisplayText(msg, 5000);
        Log(msg);
    }

    private void Log(string msg)
    {
        try
        {
            Directory.CreateDirectory("scripts");
            File.AppendAllText("scripts\\MissionSkipF9.log", DateTime.Now.ToString("HH:mm:ss") + " " + msg + "\r\n");
        }
        catch { }
    }

    class CodeImage
    {
        public IntPtr Base;
        public byte[] Code;
        public int Size;
        public int NativeAt;
        public int EnterAt;
    }

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
    static extern int VirtualQuery(IntPtr lpAddress, out MEMORY_BASIC_INFORMATION lpBuffer, uint dwLength);
    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int nSize, out int lpNumberOfBytesRead);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int nSize, out int lpNumberOfBytesWritten);
}
