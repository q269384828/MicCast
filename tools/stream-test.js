// 握手成功后再持续推送 StreamData（20ms/帧 静音 PCM），验证服务端是否接受手机→PC 音频通道。
const net = require('net');

const CONTROL_PORT = 59100;
const AUDIO_PORT = 59100;
const FRAME_BYTES = 3840;

function varint(n) {
  const out = []; let v = n;
  while (v > 0x7f) { out.push((v & 0x7f) | 0x80); v = Math.floor(v / 128); }
  out.push(v); return Buffer.from(out);
}
const tag = (f, w) => varint((f << 3) | w);
const vint = (f, v) => Buffer.concat([tag(f, 0), varint(v)]);
const str = (f, s) => { const b = Buffer.from(s, 'utf8'); return Buffer.concat([tag(f, 2), varint(b.length), b]); };
const bytes = (f, b) => Buffer.concat([tag(f, 2), varint(b.length), b]);
const delim = (b) => Buffer.concat([varint(b.length), b]);

function readFields(buf) {
  const out = []; let i = 0;
  const rv = () => { let r = 0, s = 0, b; do { b = buf[i++]; r += (b & 0x7f) * Math.pow(2, s); s += 7; } while (b & 0x80); return r; };
  while (i < buf.length) {
    const t = rv(); const field = t >> 3, wire = t & 7; let value = null;
    if (wire === 0) value = rv();
    else if (wire === 2) { const len = rv(); value = buf.subarray(i, i + len); i += len; }
    else if (wire === 5) { value = buf.readUInt32LE(i); i += 4; }
    else if (wire === 1) { value = Number(buf.readBigInt64LE(i)); i += 8; }
    else break;
    out.push({ field, wire, value });
  }
  return out;
}

function buildClientConfig() {
  const features = Buffer.concat([
    bytes(1, vint(1, 0)),
    bytes(2, vint(1, 1)),
    bytes(3, Buffer.concat([vint(1, 0), vint(2, 0)])),
    bytes(4, vint(1, 1)),
    bytes(5, vint(1, 1)),
    bytes(7, vint(1, 1))
  ]);
  const props = Buffer.concat([str(1, 'MicCast (HarmonyOS)'), vint(2, 0)]);
  return Buffer.concat([
    str(1, 'MicCast (HarmonyOS)'), str(3, '1.0.0'),
    vint(6, 48000), vint(7, 16), vint(8, 2), bytes(9, features), vint(10, 0),
    vint(13, 48000), vint(14, 16), vint(15, 2), str(16, 'HarmonyOS 6.0'), vint(17, 5), bytes(18, props)
  ]);
}

function buildStreamData(tick, ts, payload) {
  return Buffer.concat([vint(1, tick), vint(2, ts), bytes(3, payload), vint(4, payload.length)]);
}

const dgram = require('dgram');
const udp = dgram.createSocket('udp4');
const silence = Buffer.alloc(FRAME_BYTES, 0);

let rx = Buffer.alloc(0);
let step = 0;
let tick = 0;
let framesSent = 0;
let disconnects = 0;
let stops = 0;
let retransRequests = 0;

const socket = net.connect({ host: '127.0.0.1', port: CONTROL_PORT }, () => console.log('[tcp] connected'));

socket.on('data', (chunk) => {
  rx = Buffer.concat([rx, chunk]);
  while (rx.length > 0) {
    let i = 0, len = 0, shift = 0, b;
    do { if (i >= rx.length) return; b = rx[i++]; len += (b & 0x7f) * Math.pow(2, shift); shift += 7; } while (b & 0x80);
    if (rx.length < i + len) return;
    const frame = rx.subarray(i, i + len);
    rx = rx.subarray(i + len);

    if (step === 0) {
      socket.write(delim(buildClientConfig()));
      console.log('[hs] client config sent');
      step = 1;
    } else if (step === 1) {
      console.log('[hs] features received, starting UDP audio');
      step = 2;
      udp.send(Buffer.alloc(0), AUDIO_PORT, '127.0.0.1', () => { });
      const timer = setInterval(() => {
        const sd = buildStreamData(tick, Date.now(), silence);
        tick++;
        framesSent++;
        udp.send(sd, AUDIO_PORT, '127.0.0.1');
      }, 20);
      setTimeout(() => {
        clearInterval(timer);
        console.log(`[audio] sent ${framesSent} frames (${(framesSent * 20)} ms of audio)`);
        console.log(`[result] disconnects=${disconnects} stops=${stops} retrans_requests=${retransRequests}`);
        const ok = disconnects === 0 && stops === 0 && framesSent > 50;
        console.log(ok ? '[result] SERVER ACCEPTED AUDIO STREAM ✅' : '[result] STREAM REJECTED ❌');
        socket.end();
        udp.close();
        process.exit(ok ? 0 : 3);
      }, 3000);
    } else {
      const fields = readFields(frame).map((x) => x.field);
      if (fields.includes(1)) { disconnects++; console.log('[msg] disconnect_client received'); }
      if (fields.includes(12)) { stops++; console.log('[msg] stop received'); }
      if (fields.includes(9)) { retransRequests++; console.log('[msg] retransmission_request received'); }
      if (fields.includes(21)) {
        const inner = readFields(frame).find((x) => x.field === 21);
        const ts = readFields(inner.value).find((x) => x.field === 1);
        socket.write(delim(bytes(22, vint(1, ts ? ts.value : 0))));
      }
    }
  }
});

socket.on('error', (e) => console.log(`[tcp] error: ${e.message}`));
setTimeout(() => { console.log('[result] TIMEOUT'); process.exit(2); }, 25000);

