// vcde-menu[mem][fs].js
// God mode (F1) and clean mission skip (F7) for GTA Vice City - The Definitive Edition.
// Host vc_unreal, CLEO Redux 1.5.x. Keep [mem] in the file name: the skip reads and patches
// the loaded mission code in CTheScripts::ScriptSpace.
// HOW THIS SKIP WORKS
//   Every Vice City mission is laid out as
//       [SCRIPT_NAME]  GOSUB mission_body
//       R: IF HAS_DEATHARREST_BEEN_EXECUTED  GOSUB mission_failed
//       T: GOSUB mission_cleanup  TERMINATE_THIS_SCRIPT
//   and the body jumps to its pass label, which RETURNs to R. FAIL_CURRENT_MISSION makes the
//   engine pop that outermost GOSUB on the next frame (CRunningScript::Process: mission thread,
//   stack depth 1), so the thread continues at R. Before asking for that, the script rewrites the
//   bytes between R and T (in the loaded copy of the mission only) so that R runs the mission's
//   own pass block and then falls into the untouched cleanup + terminate at T. The mission ends
//   exactly the way it ends when you pass it normally: pass rewards, next-mission threads,
//   cleanup, MISSION_HAS_FINISHED, thread terminated. No reload, nothing orphaned in the save.
//   The bytes after T are never touched, so a failed-mission routine that is already running
//   still returns into the original code.
//
//   The pass block and header are found by disassembling the loaded mission with the Sanny
//   Builder Library command table below and following its control flow from the entry point,
//   so only code that really belongs to the running mission is used. If anything does not match
//   the expected layout, the skip refuses and logs why instead of guessing.
//
//   A guard is written in front of the pass call: a "$var = value" that the pass block is
//   certain to have executed before it can pause or return (usually its $PASSED_ flag). If the
//   mission happens to pass by itself while the skip is pending, the guard sees the value and
//   only runs the cleanup, so pass rewards and next-mission threads are never started twice.
//
// LIMITS
//   Story and strand missions (every mission that has a REGISTER_MISSION_PASSED block) can be
//   skipped. Not skippable, and reported as such: the opening missions and An Old Friend (no
//   mission header), the property-purchase cutscenes, and side activities without a pass block
//   (taxi, paramedic, firefighter, vigilante, pizza, races, stadium events, checkpoints, RC,
//   weapon range, Cone Crazy).
//   The engine only pops the mission while its thread is paused at the outer level of the body.
//   During some cutscenes the thread sits inside a subroutine for a while; the script keeps
//   retrying for 15 seconds and then puts the original bytes back.
//   If you press F7 while a "Mission failed" sequence is already playing, or you die in the same
//   instant, the mission still ends as passed, but the game may also offer its retry prompt.
//   Declining keeps the pass.
//   Saves made with the old script after a skip can already contain the orphaned thread. Use a
//   save from before that.

const HOST_VC_DE = "vc_unreal";
const PLAYER_ID = 0;
const MAX_HEALTH = 150;
const KEY_GOD = 112; // F1
const KEY_SKIP = 118; // F7

// Mission slot: CTheScripts::ScriptSpace[SIZE_MAIN_SCRIPT], SIZE_MISSION_SCRIPT bytes.
// PC/reVC SIZE_MAIN_SCRIPT = 225512, PS2 = 205512, SIZE_MISSION_SCRIPT = 35000.
// The slot is verified by decoding a mission header there; a bounded scan is the fallback.
const SLOT_CANDIDATES = [225512, 205512];
const SLOT_SIZE = 35000;
const SCAN_LIMIT = 300000;
const SCAN_CHUNK = 16384;

const FAIL_RETRY_MS = 150; // never ask on consecutive frames, see tickSkip
const FAIL_MIN_FRAMES = 3;
const SKIP_TRY_MS = 15000;
const SKIP_GRACE_MS = 5000;
const SKIP_FINISH_MS = 60000;
const TEXT_KEY = "VCDESKP";

const OP_NOP = 0x0000;
const OP_WAIT = 0x0001;
const OP_GOTO = 0x0002;
const OP_SET_VAR_INT = 0x0004;
const OP_IS_INT_VAR_EQUAL_TO_NUMBER = 0x0038;
const OP_GOTO_IF_TRUE = 0x004c;
const OP_GOTO_IF_FALSE = 0x004d;
const OP_TERMINATE_THIS_SCRIPT = 0x004e;
const OP_GOSUB = 0x0050;
const OP_RETURN = 0x0051;
const OP_IF = 0x00d6;
const OP_HAS_DEATHARREST_BEEN_EXECUTED = 0x0112;
const OP_MESSAGE_WAIT = 0x02a1;
const OP_REGISTER_MISSION_PASSED = 0x0318;
const OP_SCRIPT_NAME = 0x03a4;

// 1437 command signatures from Sanny Builder Library vc_unreal.json v0.320 ("default" set).
// One char per command id; char code - 48 indexes SIG_LIST. "." = unknown id.
// n = typed value (type byte + data), r = raw 8-byte text, v = typed values until a 0 byte.
const SIG_LIST = ["", "n", "r", "nn", "nr", "nv", "nnn", "nnr", "rnn", "nnnn", "nnnr", "nnrn", "rnnn", "rrnn", "nnnnn", "nnnrn", "nnrnn", "rnnnn", "nnnnnn", "nnnnrn", "rnnnnn", "nnnnnnn", "rnnnnnn", "nnnnnnnn", "nnnnnnnnn", "rnnnnnnnn", "nnnnnnnnnn", "nnnnnnnnnnn", "nnnnnnnnnnnn", "nnnnnnnnnnnnn", "rnnnnnnnnnnnn"];
const SIG_MAP =
  "0111333333333333333333333333333333333333333333333333333333333000003333300000110510B>99BG33333333" +
  "3333333333333333333333333333333333333333333333333333111111B130B1990BG>1911990333BG00000000888003" +
  "36900000000000000000001103333331130BBBBBB>>>BBBBBB>>>GGGGGGBBBGGGGGGBBB>1333>3331100600111301000" +
  "0413001009900000000000030000000000000000000M303131N040066600N10B9990133B3630999.3333333300600000" +
  "1003333339939>111111900BBBBBGGGGGBBBBBGGGGGBGBBGG66031103309919031111101>33330333333330033000033" +
  "169<<<0BB3313100100113030I13>>>BBB>>>BBB66319133360B11380J0310000333333306BB33311000063606004100" +
  "010363011160001H000000100000000000000000000000000000000000000000000000000000000003013010300>0001" +
  "0001000>>33BBEEEEEEHHHHHH0000001BB9000000011109911091B0330030A111>332340100061000034003333300A0D" +
  "00F00000I000111111101010230003333113833B011E3000333001330K000073911101001.0009G1304100000000>133" +
  "119B011003300A0000600091000000B00931=00000B09HG420301>1300003L3609312001106311B1110000B110B1>103" +
  "3307790100166113111E32290010301101111201001003111336130006000330E001010E100001000300300136000130" +
  "00030300300B13330303000G0000103003310030311102001009600302;@00013006G1311J0333B33>>>BBB600100010" +
  "31133000131360013003>1300000633301>339B01010113333333333>L312313B030E131010000>131>E0H1333100303" +
  "31363BG10BG6N2221101G60:M310E031436600611>E10130>020600?C3300000010667301103300001011010000031B3" +
  "31111030B1332110311000G0001>03333633193032031100>1133000113011136134101330B1199G093330116101011";

// Size of the value that follows each SCM type byte:
// 1 int32, 2 global var, 3 local var, 4 int8, 5 int16, 6 float32.
const TYPE_SIZE = [-1, 4, 2, 2, 1, 2, 4];

function sigOf(op) {
  if (op < 0 || op >= SIG_MAP.length) return null;
  const c = SIG_MAP.charCodeAt(op);
  if (c === 46) return null;
  return SIG_LIST[c - 48];
}

function u16(b, o) {
  return b[o] | (b[o + 1] << 8);
}
function u32(b, o) {
  return (b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24)) >>> 0;
}
function i32(b, o) {
  return b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24);
}
function hex(n, w) {
  let s = (n >>> 0).toString(16).toUpperCase();
  while (s.length < (w || 4)) s = "0" + s;
  return s;
}
function text8(b, o) {
  let s = "";
  for (let i = 0; i < 8 && b[o + i]; i++) s += String.fromCharCode(b[o + i]);
  return s;
}

function readTyped(m, q, t) {
  if (t === 1 || t === 6) return i32(m, q);
  if (t === 2 || t === 3) return u16(m, q);
  if (t === 4) return (m[q] << 24) >> 24;
  return (u16(m, q) << 16) >> 16;
}

// Decodes one instruction. Returns null for an unknown command or a malformed parameter.
function decode(m, at, limit) {
  if (at < 0 || at + 2 > limit) return null;
  const word = u16(m, at);
  const op = word & 0x7fff;
  const sig = sigOf(op);
  if (sig === null) return null;
  let q = at + 2;
  const params = [];
  for (let k = 0; k < sig.length; k++) {
    if (q >= limit) return null;
    const kind = sig.charCodeAt(k);
    if (kind === 114) {
      params.push({ t: 0, at: q, v: 0 });
      q += 8;
      continue;
    }
    if (kind === 110) {
      const t = m[q];
      if (t < 1 || t > 6) {
        // Text written where the library lists a number (e.g. LOAD_MISSION_AUDIO on PC data).
        if (t >= 0x20 && t < 0x7f) {
          params.push({ t: 0, at: q, v: 0 });
          q += 8;
          continue;
        }
        return null;
      }
      if (q + 1 + TYPE_SIZE[t] > limit) return null;
      params.push({ t: t, at: q + 1, v: readTyped(m, q + 1, t) });
      q += 1 + TYPE_SIZE[t];
      continue;
    }
    for (;;) {
      if (q >= limit) return null;
      const t = m[q];
      if (t === 0) {
        q++;
        break;
      }
      if (t > 6) return null;
      q += 1 + TYPE_SIZE[t];
    }
  }
  if (q > limit) return null;
  return { op: op, word: word, params: params, next: q };
}

// Mission-local jumps use negative offsets: IP = SIZE_MAIN_SCRIPT - offset (reVC CRunningScript).
function jumpTarget(d) {
  const p = d.params[0];
  if (!p || (p.t !== 1 && p.t !== 5) || p.v >= 0) return null;
  return -p.v;
}

function isJump(op) {
  return op === OP_GOTO || op === OP_GOTO_IF_FALSE || op === OP_GOTO_IF_TRUE || op === OP_GOSUB;
}

// Follows control flow from the mission entry (slot offset 0). Only reachable code is decoded,
// so bytes left over from other missions in the slot are never treated as this mission.
function reach(m, limit) {
  const ins = new Map();
  const edges = [];
  const targets = new Map();
  const work = [0];
  while (work.length) {
    let at = work.pop();
    while (!ins.has(at)) {
      if (at < 0 || at >= limit) return { ins: ins, edges: edges, targets: targets, err: "jump outside slot +" + at };
      const d = decode(m, at, limit);
      if (!d) {
        const w = at + 1 < limit ? u16(m, at) : 0;
        return { ins: ins, edges: edges, targets: targets, err: "unknown command " + hex(w & 0x7fff) + " at +" + at };
      }
      ins.set(at, d);
      if (isJump(d.op)) {
        const t = jumpTarget(d);
        if (t !== null) {
          edges.push([at, t]);
          targets.set(t, (targets.get(t) || 0) + 1);
          work.push(t);
        }
        if (d.op === OP_GOTO) break;
      }
      if (d.op === OP_RETURN || d.op === OP_TERMINATE_THIS_SCRIPT) break;
      at = d.next;
    }
  }
  return { ins: ins, edges: edges, targets: targets, err: null };
}

function sortedOffsets(ins) {
  const a = Array.from(ins.keys());
  a.sort(function (x, y) {
    return x - y;
  });
  return a;
}

function parseHeader(ins, edges) {
  let at = 0;
  const first = ins.get(0);
  if (!first) return { err: "no code at mission start" };
  if (first.op === OP_SCRIPT_NAME) at = first.next;
  const g = ins.get(at);
  if (!g || g.op !== OP_GOSUB) return { err: "mission has no GOSUB header" };
  const start = jumpTarget(g);
  if (start === null) return { err: "header GOSUB target is not mission-local" };
  const R = g.next;
  const region = [];
  let deathArrest = false;
  let p = R;
  for (;;) {
    const d = ins.get(p);
    if (!d) return { err: "header region is not straight code" };
    region.push(p);
    if (d.op === OP_HAS_DEATHARREST_BEEN_EXECUTED) deathArrest = true;
    if (d.op === OP_TERMINATE_THIS_SCRIPT) break;
    if (region.length > 64) return { err: "header region too long" };
    p = d.next;
  }
  if (region.length < 2) return { err: "header region too short" };
  const endAt = p;
  const T = region[region.length - 2];
  const tail = ins.get(T);
  if (tail.op !== OP_GOSUB) return { err: "no cleanup GOSUB before TERMINATE" };
  const cleanup = jumpTarget(tail);
  if (cleanup === null) return { err: "cleanup target is not mission-local" };
  if (!deathArrest) return { err: "header has no HAS_DEATHARREST_BEEN_EXECUTED check" };
  for (let i = 0; i < region.length - 2; i++) {
    const d = ins.get(region[i]);
    // A GOSUB here (mission_failed) must return straight into the preserved tail.
    if (d.op === OP_GOSUB && d.next !== T) return { err: "header GOSUB returns inside the patch area" };
    if (d.op === OP_WAIT || d.op === OP_MESSAGE_WAIT) return { err: "header region yields" };
  }
  for (let i = 0; i < edges.length; i++) {
    const src = edges[i][0];
    const dst = edges[i][1];
    if (dst >= R && dst < T && !(src >= R && src < T)) return { err: "code outside the header jumps into it" };
  }
  return { err: null, R: R, T: T, endAt: endAt, start: start, cleanup: cleanup };
}

// Basic blocks that contain REGISTER_MISSION_PASSED. The block starts right after the previous
// GOTO / RETURN / TERMINATE, which is where the mission's "passed" label is.
function findPassBlocks(ins) {
  const order = sortedOffsets(ins);
  const index = new Map();
  for (let i = 0; i < order.length; i++) index.set(order[i], i);
  const out = [];
  const seen = new Set();
  for (let i = 0; i < order.length; i++) {
    if (ins.get(order[i]).op !== OP_REGISTER_MISSION_PASSED) continue;
    let j = i - 1;
    while (j >= 0) {
      const d = ins.get(order[j]);
      if (d.next !== order[j + 1]) break;
      if (d.op === OP_GOTO || d.op === OP_RETURN || d.op === OP_TERMINATE_THIS_SCRIPT) break;
      j--;
    }
    const S = order[j + 1];
    if (!seen.has(S)) {
      seen.add(S);
      out.push({ S: S, registerAt: order[i] });
    }
  }
  return { blocks: out, order: order, index: index };
}

// Guard candidates are "$var = literal" writes in the pass block that are guaranteed to have
// happened before the thread can pause inside the block or leave it:
//  - writes before the first IF / jump / GOSUB / WAIT of the block, or
//  - when the whole block has no GOSUB / WAIT and only forward jumps that stay inside it,
//    writes that no conditional jump skips over.
function isGuardWrite(d) {
  return d.op === OP_SET_VAR_INT && (d.word & 0x8000) === 0 && d.params.length === 2 && d.params[0].t === 2 &&
    (d.params[1].t === 1 || d.params[1].t === 4 || d.params[1].t === 5);
}

function passInfo(m, ins, order, index, S, cleanup, registerAt) {
  const prefixGuards = [];
  const writes = [];
  const jumps = [];
  let prefix = true;
  let yields = false;
  let callsCleanup = false;
  let gxt = "";
  let endAt = S;
  let k = index.get(S);
  let steps = 0;
  while (k !== undefined && k < order.length && steps < 4096) {
    const at = order[k];
    const d = ins.get(at);
    endAt = at;
    if (d.op === OP_IF || d.op === OP_GOTO_IF_FALSE || d.op === OP_GOTO_IF_TRUE || d.op === OP_GOSUB ||
        d.op === OP_WAIT || d.op === OP_MESSAGE_WAIT) {
      prefix = false;
    }
    if (d.op === OP_GOSUB || d.op === OP_WAIT || d.op === OP_MESSAGE_WAIT) yields = true;
    if (d.op === OP_GOTO_IF_FALSE || d.op === OP_GOTO_IF_TRUE) jumps.push([at, jumpTarget(d)]);
    if (isGuardWrite(d)) {
      const g = { off: d.params[0].v, value: d.params[1].v, at: at };
      if (prefix) prefixGuards.push(g);
      else writes.push(g);
    }
    if (d.op === OP_GOSUB && jumpTarget(d) === cleanup) callsCleanup = true;
    if (at === registerAt && d.params.length && d.params[0].t === 0) gxt = text8(m, d.params[0].at);
    if (d.op === OP_GOTO || d.op === OP_RETURN || d.op === OP_TERMINATE_THIS_SCRIPT) break;
    if (k + 1 < order.length && order[k + 1] !== d.next) break;
    k++;
    steps++;
  }
  const guards = prefixGuards.slice();
  let structured = !yields;
  for (let i = 0; i < jumps.length && structured; i++) {
    const t = jumps[i][1];
    if (t === null || t <= jumps[i][0] || t > endAt) structured = false;
  }
  if (structured) {
    for (let i = 0; i < writes.length; i++) {
      const x = writes[i].at;
      let skipped = false;
      for (let j = 0; j < jumps.length; j++) {
        if (jumps[j][0] < x && jumps[j][1] > x) skipped = true;
      }
      if (!skipped) guards.push(writes[i]);
    }
  }
  return { guards: guards, callsCleanup: callsCleanup, gxt: gxt };
}

function jumpBytes(op, target, wide) {
  const v = -target;
  if (wide) return [op & 0xff, op >> 8, 1, v & 0xff, (v >> 8) & 0xff, (v >> 16) & 0xff, (v >>> 24) & 0xff];
  return [op & 0xff, op >> 8, 5, v & 0xff, (v >> 8) & 0xff];
}
function widthsFor(target) {
  return target <= 32767 ? [false, true] : [true];
}

// Builds exactly T - R bytes. The tail at T (GOSUB cleanup, TERMINATE) is left untouched.
//
// Guarded:  NOT $guard == value      (IS_INT_VAR_EQUAL_TO_NUMBER with the NOT bit)
//           GOTO_IF_FALSE T          -> already passed: cleanup + terminate only
//           GOSUB pass_block         -> pass rewards, flags, next-mission threads
//           NOP ...                  -> falls into T: cleanup + terminate
// No IF is written: on arrival at R the thread is never inside a condition list (it can only be
// paused by WAIT / MESSAGE_WAIT / TERMINATE), so the and/or state is ANDOR_NONE and a single
// condition sets the result directly (reVC CRunningScript::UpdateCompareFlag).
//
// Unguarded: GOSUB W  NOP ...  W: GOSUB pass_block  -> T
// Pass block and cleanup then run two levels deep, where FAIL_CURRENT_MISSION cannot pop them.
function buildPatch(R, T, S, guard) {
  const avail = T - R;
  if (avail < 7) return null;
  if (guard) {
    const c = guard.value;
    const lits = [];
    if (c >= -128 && c <= 127) lits.push(4);
    if (c >= -32768 && c <= 32767) lits.push(5);
    lits.push(1);
    const jfw = widthsFor(T);
    const gsw = widthsFor(S);
    for (let a = 0; a < lits.length; a++) {
      for (let b = 0; b < jfw.length; b++) {
        for (let e = 0; e < gsw.length; e++) {
          const litLen = lits[a] === 4 ? 2 : lits[a] === 5 ? 3 : 5;
          const total = 5 + litLen + (jfw[b] ? 7 : 5) + (gsw[e] ? 7 : 5);
          if (total > avail || (avail - total) % 2 !== 0) continue;
          let out = [0x38, 0x80, 2, guard.off & 0xff, (guard.off >> 8) & 0xff, lits[a]];
          if (lits[a] === 4) out.push(c & 0xff);
          else if (lits[a] === 5) out.push(c & 0xff, (c >> 8) & 0xff);
          else out.push(c & 0xff, (c >> 8) & 0xff, (c >> 16) & 0xff, (c >>> 24) & 0xff);
          out = out.concat(jumpBytes(OP_GOTO_IF_FALSE, T, jfw[b]));
          out = out.concat(jumpBytes(OP_GOSUB, S, gsw[e]));
          while (out.length < avail) out.push(0, 0);
          return { bytes: out, layout: "guarded" };
        }
      }
    }
  }
  const gsw2 = widthsFor(S);
  for (let e = 0; e < gsw2.length; e++) {
    const gsLen = gsw2[e] ? 7 : 5;
    const W = T - gsLen;
    const gww = widthsFor(W);
    for (let b = 0; b < gww.length; b++) {
      const gwLen = gww[b] ? 7 : 5;
      const rest = avail - gwLen - gsLen;
      if (rest < 0 || rest % 2 !== 0) continue;
      let out = jumpBytes(OP_GOSUB, W, gww[b]);
      while (out.length < avail - gsLen) out.push(0, 0);
      out = out.concat(jumpBytes(OP_GOSUB, S, gsw2[e]));
      return { bytes: out, layout: "wrapped" };
    }
  }
  return null;
}

// Pure analysis of a mission slot image.
function analyzeSlot(m, limit) {
  const r = reach(m, limit);
  if (r.err) return { ok: false, slotOk: false, reason: r.err };
  const h = parseHeader(r.ins, r.edges);
  if (h.err) return { ok: false, slotOk: false, reason: h.err };
  const pb = findPassBlocks(r.ins);
  const cands = [];
  for (let i = 0; i < pb.blocks.length; i++) {
    const blk = pb.blocks[i];
    if (!r.targets.has(blk.S)) continue;
    if (blk.S >= h.R && blk.S < h.endAt + 2) continue;
    const info = passInfo(m, r.ins, pb.order, pb.index, blk.S, h.cleanup, blk.registerAt);
    if (info.callsCleanup) continue;
    cands.push({ S: blk.S, guards: info.guards, gxt: info.gxt });
  }
  if (!cands.length) return { ok: false, slotOk: true, reason: "no REGISTER_MISSION_PASSED block in this mission" };
  let pick = cands[0];
  for (let i = 0; i < cands.length; i++) {
    if (cands[i].guards.length) {
      pick = cands[i];
      break;
    }
  }
  let name = "";
  const first = r.ins.get(0);
  if (first && first.op === OP_SCRIPT_NAME && first.params.length && first.params[0].t === 0) name = text8(m, first.params[0].at);
  return {
    ok: true,
    slotOk: true,
    R: h.R,
    T: h.T,
    endAt: h.endAt,
    cleanup: h.cleanup,
    S: pick.S,
    guards: pick.guards,
    gxt: pick.gxt,
    name: name,
    passBlocks: cands.length,
  };
}

// ---------------------------------------------------------------- game side

const state = {
  godMode: false,
  lastGod: false,
  lastSkip: false,
  scriptSpace: 0,
  header: null,
  slotBase: -1,
  slotSize: SLOT_SIZE,
  analysis: null,
  analysisReason: "",
  needScan: false,
  skip: null,
  frame: 0,
  status: "Waiting for a mission",
};

function readBlock(address, size) {
  const b = new Uint8Array(size + 8);
  let i = 0;
  for (; i + 4 <= size; i += 4) {
    const v = Memory.ReadU32(address + i, false, false) >>> 0;
    b[i] = v & 0xff;
    b[i + 1] = (v >>> 8) & 0xff;
    b[i + 2] = (v >>> 16) & 0xff;
    b[i + 3] = (v >>> 24) & 0xff;
  }
  for (; i < size; i++) b[i] = Memory.ReadU8(address + i, false, false);
  return b;
}

function readGlobal(off) {
  return Memory.ReadI32(state.scriptSpace + off, false, false) | 0;
}

function notify(msg) {
  log("vcde: " + msg);
  try {
    FxtStore.insert(TEXT_KEY, msg);
    native("PRINT_HELP", TEXT_KEY);
  } catch (e) {
    // text output is optional
  }
}

// main.scm header as loaded in ScriptSpace: segment 1 = variables (its jump target is the end of
// the variable space), the segment whose id byte is 1 holds main size, largest mission size,
// mission count and the file offset of every mission.
function parseScmHeader() {
  let at = 0;
  let varEnd = 0;
  for (let n = 0; n < 6; n++) {
    const b = readBlock(state.scriptSpace + at, 24);
    if (b[0] !== 0x02 || b[1] !== 0x00 || b[2] !== 0x01) return null;
    const target = u32(b, 3);
    if (n === 0) varEnd = target;
    if (b[7] === 0x01) {
      const mainSize = u32(b, 8);
      const largest = u32(b, 12);
      const count = u16(b, 16);
      if (mainSize < 10000 || mainSize > 1000000 || largest < 1000 || largest > 70000 || count < 1 || count > 200) return null;
      const t = readBlock(state.scriptSpace + at + 20, count * 4);
      const offsets = [];
      for (let i = 0; i < count; i++) offsets.push(u32(t, i * 4));
      return { varEnd: varEnd, mainSize: mainSize, largest: largest, count: count, offsets: offsets };
    }
    if (target <= at || target > 1000000) return null;
    at = target;
  }
  return null;
}

function analyzeAt(base) {
  const img = readBlock(state.scriptSpace + base, state.slotSize);
  const a = analyzeSlot(img, state.slotSize);
  a.base = base;
  a.img = img;
  return a;
}

function scanForSlot() {
  const h = state.header;
  if (!h) return -1;
  const fileCopies = new Set(h.offsets);
  for (let start = h.mainSize; start < SCAN_LIMIT; start += SCAN_CHUNK) {
    const len = Math.min(SCAN_CHUNK + 24, SCAN_LIMIT - start);
    const b = readBlock(state.scriptSpace + start, len);
    for (let i = 7; i + 6 <= len; i++) {
      if (b[i] !== 0xd6 || b[i + 1] !== 0x00 || b[i + 2] !== 0x04 || b[i + 3] !== 0x00 || b[i + 4] !== 0x12 || b[i + 5] !== 0x01) continue;
      if (b[i - 7] !== 0x50 || b[i - 6] !== 0x00) continue;
      let base = start + i - 7;
      if (i >= 17 && b[i - 17] === 0xa4 && b[i - 16] === 0x03) base = start + i - 17;
      if (fileCopies.has(base)) continue;
      const a = analyzeAt(base);
      if (a.slotOk) return base;
    }
    wait(0);
  }
  return -1;
}

function scanMission() {
  state.analysis = null;
  if (!state.scriptSpace) {
    state.analysisReason = "CTheScripts::ScriptSpace not resolved";
    return;
  }
  if (!state.header) {
    state.header = parseScmHeader();
    if (state.header) {
      if (state.header.largest > state.slotSize) state.slotSize = state.header.largest;
      log("vcde: main.scm main " + state.header.mainSize + ", largest mission " + state.header.largest + ", missions " + state.header.count);
    } else {
      log("vcde: main.scm header not parsed, using fixed slot candidates only");
    }
  }
  if (state.slotBase < 0) {
    let lastReason = "";
    for (let i = 0; i < SLOT_CANDIDATES.length && state.slotBase < 0; i++) {
      const c = SLOT_CANDIDATES[i];
      if (state.header && c < state.header.mainSize) continue;
      const a = analyzeAt(c);
      if (a.slotOk) {
        state.slotBase = c;
      } else {
        lastReason = a.reason;
      }
    }
    if (state.slotBase < 0) {
      log("vcde: no mission header at fixed slot offsets (" + lastReason + "), scanning");
      state.slotBase = scanForSlot();
    }
    if (state.slotBase < 0) {
      state.analysisReason = "mission slot not found in ScriptSpace";
      return;
    }
    log("vcde: mission slot at ScriptSpace+" + state.slotBase);
  }
  const a = analyzeAt(state.slotBase);
  if (!a.ok) {
    state.analysisReason = a.reason;
    log("vcde: mission not skippable: " + a.reason);
    return;
  }
  state.analysis = a;
  state.analysisReason = "";
  log("vcde: mission " + (a.name || "?") + " pass block +" + a.S + " (" + a.gxt + "), header +" + a.R + "..+" + a.T +
      ", cleanup +" + a.cleanup + ", guards " + a.guards.length);
}

function bytesEqual(a, ao, b, bo, n) {
  for (let i = 0; i < n; i++) if (a[ao + i] !== b[bo + i]) return false;
  return true;
}

function writeBytes(address, bytes) {
  for (let i = 0; i < bytes.length; i++) Memory.WriteU8(address + i, bytes[i], false, false);
}

function startSkip() {
  if (!ONMISSION) {
    notify("Not on a mission");
    return;
  }
  scanMission(); // always re-read: the slot must be the mission that is running now
  const a = state.analysis;
  if (!a) {
    notify("This mission cannot be skipped: " + state.analysisReason);
    return;
  }
  let guard = null;
  for (let i = 0; i < a.guards.length; i++) {
    const g = a.guards[i];
    if (state.header && (g.off < 8 || g.off + 4 > state.header.varEnd)) continue;
    if (readGlobal(g.off) !== g.value) {
      guard = g;
      break;
    }
  }
  const patch = buildPatch(a.R, a.T, a.S, guard);
  if (!patch) {
    notify("No room to patch this mission header");
    return;
  }
  const at = state.scriptSpace + a.base + a.R;
  const original = Array.prototype.slice.call(a.img, a.R, a.T);
  const now = readBlock(at, original.length);
  if (!bytesEqual(now, 0, original, 0, original.length)) {
    notify("Mission code changed, try again");
    return;
  }
  writeBytes(at, patch.bytes);
  const check = readBlock(at, patch.bytes.length);
  if (!bytesEqual(check, 0, patch.bytes, 0, patch.bytes.length)) {
    writeBytes(at, original);
    notify("Could not write the mission patch");
    return;
  }
  TIMERB = 0;
  state.skip = {
    phase: "trying",
    analysis: a,
    guard: guard,
    address: at,
    patch: patch.bytes,
    original: original,
    lastAsk: -100000,
    lastAskFrame: -100,
    asks: 0,
    since: 0,
  };
  log("vcde: patched +" + a.R + " (" + patch.layout + (guard ? ", guard $" + guard.off / 4 + "==" + guard.value : "") + ")");
  notify("Skipping " + (a.gxt || a.name || "mission") + "...");
}

function restorePatch(sk) {
  const cur = readBlock(sk.address, sk.patch.length);
  if (bytesEqual(cur, 0, sk.patch, 0, sk.patch.length)) {
    writeBytes(sk.address, sk.original);
    return true;
  }
  return false;
}

function guardHit(sk) {
  return sk.guard !== null && readGlobal(sk.guard.off) === sk.guard.value;
}

function finishSkip(sk) {
  const passed = sk.guard === null ? null : readGlobal(sk.guard.off) === sk.guard.value;
  const label = sk.analysis.gxt || sk.analysis.name || "mission";
  if (passed === false) notify("Mission ended without the pass block running (" + label + ")");
  else notify("Mission passed: " + label);
  log("vcde: skip finished after " + sk.asks + " request(s), " + TIMERB + " ms");
  state.skip = null;
  state.analysis = null;
}

// FAIL_CURRENT_MISSION sets CTheScripts::FailCurrentMission = 2. CTheScripts::Process decrements
// it once per frame before running scripts, and the mission thread pops its outer GOSUB only
// while the value is exactly 1 and its stack depth is 1. Asking again on the very next frame
// could keep the value at 2 for the mission thread, so requests are spaced several frames apart
// and stop as soon as the guard shows the pass block has started.
function tickSkip() {
  const sk = state.skip;
  if (!sk) return;
  const t = TIMERB;
  if (!ONMISSION) {
    finishSkip(sk);
    return;
  }
  if (sk.phase === "trying") {
    if (guardHit(sk)) {
      sk.phase = "passing";
      sk.since = t;
      return;
    }
    if (t >= SKIP_TRY_MS) {
      sk.phase = "grace";
      sk.since = t;
      return;
    }
    if (t - sk.lastAsk >= FAIL_RETRY_MS && state.frame - sk.lastAskFrame >= FAIL_MIN_FRAMES) {
      native("FAIL_CURRENT_MISSION");
      sk.lastAsk = t;
      sk.lastAskFrame = state.frame;
      sk.asks++;
    }
    return;
  }
  if (sk.phase === "grace") {
    if (guardHit(sk)) {
      sk.phase = "passing";
      sk.since = t;
      return;
    }
    if (t - sk.since >= SKIP_GRACE_MS) {
      const restored = restorePatch(sk);
      log("vcde: mission thread never returned to its top level, " + (restored ? "original bytes restored" : "slot changed, nothing restored"));
      notify("Could not skip right now (mission busy in a cutscene?). Try F7 again in a moment.");
      state.skip = null;
      state.analysis = null;
    }
    return;
  }
  if (sk.phase === "passing" && t - sk.since >= SKIP_FINISH_MS) {
    log("vcde: pass block started but ONMISSION is still set after " + SKIP_FINISH_MS + " ms");
    state.skip = null;
    state.analysis = null;
  }
}

function edge(code, key) {
  const down = Pad.IsKeyPressed(code);
  const hit = down && !state[key];
  state[key] = down;
  return hit;
}

function applyGod(player) {
  if (!state.godMode) return;
  if (!native("IS_PLAYER_PLAYING", PLAYER_ID)) return;
  player.setHealth(MAX_HEALTH);
  native("ADD_ARMOUR_TO_PLAYER", PLAYER_ID, 200); // engine clamps to the player's max armour
}

function main() {
  state.scriptSpace = Memory.Translate("CTheScripts::ScriptSpace");
  log("vcde: ScriptSpace 0x" + state.scriptSpace.toString(16));
  if (!state.scriptSpace) notify("CTheScripts::ScriptSpace not found, skip disabled");
  const player = new Player(PLAYER_ID);
  let wasOnMission = false;
  while (true) {
    wait(0);
    state.frame++;
    if (edge(KEY_GOD, "lastGod")) {
      state.godMode = !state.godMode;
      notify(state.godMode ? "God mode on" : "God mode off");
    }
    applyGod(player);

    const on = !!ONMISSION;
    if (on && !wasOnMission && !state.skip) {
      state.analysis = null;
      state.needScan = true;
    }
    if (!on && !state.skip) {
      state.analysis = null;
      state.needScan = false;
    }
    wasOnMission = on;
    if (state.needScan && on && !state.skip) {
      state.needScan = false;
      scanMission();
    }

    if (edge(KEY_SKIP, "lastSkip")) {
      if (state.skip) notify("Skip already in progress");
      else startSkip();
    }
    tickSkip();

    if (state.skip) state.status = "Skipping (" + state.skip.phase + ")";
    else if (!on) state.status = "Waiting for a mission";
    else if (state.analysis) state.status = "F7 ready: " + (state.analysis.gxt || state.analysis.name);
    else state.status = "Not skippable: " + state.analysisReason;

    if (typeof ImGui !== "undefined") {
      ImGui.BeginFrame("vcde-menu");
      ImGui.Begin("VC DE", true, false, false, false, true);
      ImGui.Text("F1 god   F7 skip mission");
      ImGui.Text(state.status);
      state.godMode = ImGui.Checkbox("God mode", state.godMode);
      if (on && !state.skip && ImGui.Button("Skip current mission", 220, 28)) startSkip();
      ImGui.End();
      ImGui.EndFrame();
    }
  }
}

if (HOST === HOST_VC_DE) {
  main();
} else {
  log("vcde menu: expected vc_unreal, got " + HOST);
}
