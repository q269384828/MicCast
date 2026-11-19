// 局域网快速端口探测：找鸿蒙手机的无线调试端口
const net = require('net');

const HOSTS = process.argv.slice(2);
const CONCURRENCY = 800;
const TIMEOUT_MS = 350;
const PORT_MIN = 1024;
const PORT_MAX = 65535;

function probe(host, port) {
  return new Promise((resolve) => {
    const sock = new net.Socket();
    let done = false;
    const finish = (open) => {
      if (done) return;
      done = true;
      sock.destroy();
      resolve(open ? port : 0);
    };
    sock.setTimeout(TIMEOUT_MS);
    sock.once('connect', () => finish(true));
    sock.once('timeout', () => finish(false));
    sock.once('error', () => finish(false));
    sock.connect(port, host);
  });
}

async function scanHost(host) {
  const open = [];
  let port = PORT_MIN;
  const workers = [];
  const total = PORT_MAX - PORT_MIN + 1;

  const worker = async () => {
    while (true) {
      const p = port++;
      if (p > PORT_MAX) return;
      const r = await probe(host, p);
      if (r) {
        open.push(r);
        console.log(`  [open] ${host}:${r}`);
      }
    }
  };

  const started = Date.now();
  for (let i = 0; i < CONCURRENCY; i++) workers.push(worker());
  await Promise.all(workers);

  const secs = ((Date.now() - started) / 1000).toFixed(1);
  console.log(`[${host}] scanned ${total} ports in ${secs}s -> open: ${open.length ? open.sort((a, b) => a - b).join(',') : 'none'}`);
  return { host, open };
}

(async () => {
  if (HOSTS.length === 0) {
    console.log('usage: node lan-scan.js <ip> [ip...]');
    process.exit(1);
  }
  console.log(`scanning ${HOSTS.length} host(s), ports ${PORT_MIN}-${PORT_MAX}, concurrency ${CONCURRENCY}`);
  const results = [];
  for (const h of HOSTS) {
    results.push(await scanHost(h));
  }
  console.log('\n=== summary ===');
  for (const r of results) {
    console.log(`${r.host}: ${r.open.length ? r.open.join(',') : 'no open ports'}`);
  }
})();
