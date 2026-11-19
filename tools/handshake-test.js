// 与鸿蒙端完全相同的协议逻辑，用于对本机官方 AudioRelay 0.27.5 做真实握手验证。
const net = require('net');
const dgram = require('dgram');

const CONTROL_PORT = 59100;
const DISCOVERY_PORT = 59200;

function varint(n) {
  const out = [];
  let v = n;
  while (v > 0x7f) {
    out.push((v & 0x7f) | 0x80);
    v = Math.floor(v / 128);
  }
  out.push(v);
  return Buffer.from(out);
}
const tag = (f, w) => varint((f << 3) | w);
const vint = (f, v) => Buffer.concat([tag(f, 0), varint(v)]);
const str = (f, s) => {
  const b = Buffer.from(s, 'utf8');
  return Buffer.concat([tag(f, 2), varint(b.length), b]);
};
const bytes = (f, b) => Buffer.concat([tag(f, 2), varint(b.length), b]);
const delim = (b) => Buffer.concat([varint(b.length), b]);

function readFields(buf) {
  const out = [];
  let i = 0;
  const readVarint = () => {
    let r = 0, s = 0, b;
    do { b = buf[i++]; r += (b & 0x7f) * Math.pow(2, s); s += 7; } while (b & 0x80);
    return r;
  };
  while (i < buf.length) {
    const t = readVarint();
    const field = t >> 3, wire = t & 7;
    let value = null;
    if (wire === 0) value = readVarint();
    else if (wire === 2) { const len = readVarint(); value = buf.subarray(i, i + len); i += len; }
    else if (wire === 5) { value = buf.readUInt32LE(i); i += 4; }
    else if (wire === 1) { value = Number(buf.readBigInt64LE(i)); i += 8; }
    else break;
    out.push({ field, wire, value });
  }
  return out;
}

function buildClientConfig() {
  const multi = vint(1, 0);
  const streamData = vint(1, 1);
  const compression = Buffer.concat([vint(1, 0), vint(2, 0)]);
  const messages = vint(1, 1);
  const retrans = vint(1, 1);
  const serverCfg = vint(1, 1);
  const features = Buffer.concat([
    bytes(1, multi), bytes(2, streamData), bytes(3, compression), bytes(4, messages),
    bytes(5, retrans), bytes(7, serverCfg)
  ]);
  const props = Buffer.concat([str(1, 'MicCast (HarmonyOS)'), vint(2, 0)]);
  return Buffer.concat([
    str(1, 'MicCast (HarmonyOS)'),
    str(3, '1.0.0'),
    vint(6, 48000), vint(7, 16), vint(8, 2),
    bytes(9, features),
    vint(10, 0),
    vint(13, 48000), vint(14, 16), vint(15, 2),
    str(16, 'HarmonyOS 6.0'),
    vint(17, 5),
    bytes(18, props)
  ]);
}

function describeServerConfig(buf) {
  const f = readFields(buf);
  const get = (n) => { const x = f.find((v) => v.field === n); return x ? x.value : undefined; };
  const name = get(1) ? get(1).toString('utf8') : '';
  const version = get(2) ? get(2).toString('utf8') : '';
  const osVersion = get(3) ? get(3).toString('utf8') : '';
  const os = get(5);
  return `name="${name}" version="${version}" osVersion="${osVersion}" os=${os}`;
}

function describeFeatures(buf) {
  const f = readFields(buf);
  const parts = [];
  for (const item of f) {
    if (item.wire !== 2) continue;
    const sub = readFields(item.value);
    const activated = sub.find((s) => s.field === 1);
    let extra = '';
    const bitrate = sub.find((s) => s.field === 2);
    if (item.field === 3 && bitrate) extra = ` bitrate=${bitrate.value}`;
    parts.push(`${item.field}:${activated ? activated.value : '?'}${extra}`);
  }
  return parts.join(' ');
}

const udp = dgram.createSocket({ type: 'udp4', reuseAddr: true });
let discovered = 0;
udp.on('message', (msg, rinfo) => {
  if (msg.length < 5) return;
  const len = msg.readInt32LE(0);
  if (len <= 0 || len + 4 > msg.length) return;
  discovered++;
  console.log(`[discovery] from ${rinfo.address}:${rinfo.port} len=${len} -> ${describeServerConfig(msg.subarray(4, 4 + len))}`);
});
udp.bind(DISCOVERY_PORT, () => console.log(`[discovery] listening udp/${DISCOVERY_PORT}`));

let rx = Buffer.alloc(0);
let step = 0;
const socket = net.connect({ host: '127.0.0.1', port: CONTROL_PORT }, () => {
  console.log('[tcp] connected');
});

socket.on('data', (chunk) => {
  rx = Buffer.concat([rx, chunk]);
  while (rx.length > 0) {
    let i = 0, len = 0, shift = 0, b;
    do {
      if (i >= rx.length) return;
      b = rx[i++]; len += (b & 0x7f) * Math.pow(2, shift); shift += 7;
    } while (b & 0x80);
    if (rx.length < i + len) return;
    const frame = rx.subarray(i, i + len);
    rx = rx.subarray(i + len);

    if (step === 0) {
      console.log(`[hs] ProtoServerConfig: ${describeServerConfig(frame)}`);
      socket.write(delim(buildClientConfig()));
      console.log('[hs] ProtoClientConfig sent');
      step = 1;
    } else if (step === 1) {
      console.log(`[hs] ProtoFeatures raw: ${describeFeatures(frame)}`);
      step = 2;
      const ping = bytes(21, vint(1, Date.now()));
      socket.write(delim(ping));
      console.log('[hs] ping_request sent');
      setTimeout(() => {
        console.log(`[hs] discovery datagrams seen: ${discovered}`);
        console.log('[hs] RESULT: handshake completed');
        socket.end();
        udp.close();
        process.exit(0);
      }, 2500);
    } else {
      const f = readFields(frame);
      const head = f.map((x) => x.field).join(',');
      console.log(`[hs] post-handshake message fields=[${head}]`);
    }
  }
});

socket.on('error', (err) => { console.log(`[tcp] error: ${err.message}`); });
socket.on('close', () => console.log('[tcp] closed'));

setTimeout(() => {
  console.log('[hs] TIMEOUT');
  process.exit(2);
}, 20000);
