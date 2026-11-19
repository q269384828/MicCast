// PC 端客户端：模拟 AudioRelay 播放器连接手机服务端，接收麦克风音频并计算音量
const net = require('net');
const dgram = require('dgram');

const PHONE_IP = process.argv[2] || '127.0.0.1';
const CONTROL_PORT = 59100;
const AUDIO_PORT = 59100;

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
    bytes(1, vint(1, 0)),                       // multi_payload off
    bytes(2, vint(1, 1)),                       // stream_data on
    bytes(3, Buffer.concat([vint(1, 0), vint(2, 0)])), // compression off
    bytes(4, vint(1, 1)),                       // messages on
    bytes(5, vint(1, 1)),                       // retransmission on
    bytes(7, vint(1, 1))                        // server_audio_config on
  ]);
  const props = Buffer.concat([str(1, 'PC Player (test)'), vint(2, 0)]);
  return Buffer.concat([
    str(1, 'PC Player (test)'), str(3, '0.27.5'),
    vint(6, 48000), vint(7, 16), vint(8, 2),
    bytes(9, features), vint(10, 0),
    vint(13, 48000), vint(14, 16), vint(15, 2),
    str(16, 'Windows 11'), vint(17, 1), bytes(18, props)
  ]);
}

function parseStreamData(buf) {
  const f = readFields(buf);
  const get = (n) => { const x = f.find((v) => v.field === n); return x ? x.value : undefined; };
  return { tick: get(1), payload: get(3) };
}

function rms16(pcm) {
  if (!pcm || pcm.length < 4) return 0;
  const n = Math.floor(pcm.length / 2);
  let sum = 0;
  for (let i = 0; i < n; i++) {
    const s = pcm.readInt16LE(i * 2) / 32768;
    sum += s * s;
  }
  return Math.sqrt(sum / n);
}

const udp = dgram.createSocket({ type: 'udp4', reuseAddr: true });
let frames = 0, rmsSum = 0, rmsMax = 0, nonZero = 0, listenOk = false;

udp.on('message', (msg) => {
  frames++;
  const sd = parseStreamData(msg);
  if (sd.payload) {
    const r = rms16(sd.payload);
    rmsSum += r;
    if (r > rmsMax) rmsMax = r;
    if (r > 0.0005) nonZero++;
  }
});

udp.bind(AUDIO_PORT, () => { listenOk = true; console.log(`[udp] bound local ${AUDIO_PORT}, waiting for phone audio`); });

let rx = Buffer.alloc(0);
let step = 0;
const sock = net.connect({ host: PHONE_IP, port: CONTROL_PORT }, () => console.log(`[tcp] connected ${PHONE_IP}:${CONTROL_PORT}`));

sock.on('data', (chunk) => {
  rx = Buffer.concat([rx, chunk]);
  while (rx.length > 0) {
    let i = 0, len = 0, shift = 0, b;
    do { if (i >= rx.length) return; b = rx[i++]; len += (b & 0x7f) * Math.pow(2, shift); shift += 7; } while (b & 0x80);
    if (rx.length < i + len) return;
    const frame = rx.subarray(i, i + len);
    rx = rx.subarray(i + len);

    if (step === 0) {
      const f = readFields(frame);
      const name = f.find((x) => x.field === 1);
      const ver = f.find((x) => x.field === 2);
      console.log(`[hs] ProtoServerConfig name="${name ? name.value.toString('utf8') : ''}" version="${ver ? ver.value.toString('utf8') : ''}"`);
      sock.write(delim(buildClientConfig()));
      console.log('[hs] ProtoClientConfig sent');
      step = 1;
    } else if (step === 1) {
      const parts = readFields(frame).filter((x) => x.wire === 2).map((x) => x.field);
      console.log(`[hs] ProtoFeatures fields=${parts.join(',')}`);
      step = 2;
    }
  }
});

sock.on('error', (e) => console.log(`[tcp] error: ${e.message}`));
sock.on('close', () => console.log('[tcp] closed'));

const SECONDS = Number(process.argv[3] || 5);
setTimeout(() => {
  console.log(`\n=== 结果（${SECONDS}s）===`);
  console.log(`UDP 绑定成功: ${listenOk}`);
  console.log(`收到音频帧: ${frames}`);
  if (frames > 0) {
    console.log(`平均 RMS: ${(rmsSum / frames).toFixed(5)}`);
    console.log(`峰值 RMS: ${rmsMax.toFixed(5)}`);
    console.log(`非静音帧: ${nonZero}/${frames}`);
    console.log(frames > 20 && rmsMax > 0.0005 ? '[结论] 手机麦克风音频已成功送达 ✅' : '[结论] 收到数据但疑似静音（对着手机说话再测）');
  } else {
    console.log('[结论] 没收到任何 UDP 音频 ❌');
  }
  try { sock.end(); udp.close(); } catch (e) { }
  process.exit(0);
}, SECONDS * 1000);
