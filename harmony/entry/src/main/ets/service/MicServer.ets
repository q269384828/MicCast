import { socket, connection } from '@kit.NetworkKit';
import { concatBytes, encodeDelimited, tryParseDelimited, DelimitedResult } from '../model/Pb';
import {
  buildDiscoveryDatagram,
  buildNotFoundRetransmission,
  buildPingRequest,
  buildPingResponse,
  buildRetransmissionResponse,
  buildServerAudioConfigResponse,
  buildServerConfig,
  buildServerFeatures,
  CONTROL_PORT,
  DISCOVERY_PORT,
  parseMessage,
  ParsedMessage
} from '../model/Messages';
import { toHex } from '../model/Pb';
import { AudioSender } from './AudioSender';
import { Logger } from './Logger';

export enum ServerState {
  Stopped = 0,
  Listening = 1,
  ClientConnected = 2
}

export function serverStateText(state: ServerState): string {
  if (state === ServerState.Listening) {
    return '等待电脑连接';
  }
  if (state === ServerState.ClientConnected) {
    return '正在串流';
  }
  return '未启动';
}

/**
 * 手机作为服务端：电脑端 MicCast Receiver
 * 连上来后，本端把麦克风 PCM 以 StreamData 推到客户端绑定的 UDP 59100。
 */
export class MicServer {
  state: ServerState = ServerState.Stopped;
  clientIp: string = '';
  deviceName: string = 'HarmonyOS Phone';
  /** 本机在局域网里的 IPv4，供电脑端手动输入时参考 */
  localIp: string = '';
  /** 本机 IPv4 的前缀长度（掩码位数），用于正确算出子网广播地址 */
  localPrefixLength: number = 24;
  lastError: string = '';

  private tcpServer: socket.TCPSocketServer | undefined = undefined;
  private conn: socket.TCPSocketConnection | undefined = undefined;
  private disc: socket.UDPSocket | undefined = undefined;
  private discTimer: number = -1;
  private rx: Uint8Array = new Uint8Array(0);
  private handshakeStep: number = 0;
  private stateHandler: () => void = () => {
  };
  private clientHandler: (ip: string) => void = () => {
  };
  private disconnectHandler: () => void = () => {
  };
  private senderRef: AudioSender | undefined = undefined;
  private pingTimer: number = -1;
  inMsgCount: number = 0;
  retransRequests: number = 0;

  /** 重传需要读取已发送帧的环形缓存 */
  setSender(sender: AudioSender): void {
    this.senderRef = sender;
  }

  setStateHandler(handler: () => void): void {
    this.stateHandler = handler;
  }

  setClientHandler(handler: (ip: string) => void): void {
    this.clientHandler = handler;
  }

  /** 电脑断开时回调，供 UI 停掉麦克风采集与后台长时任务 */
  setDisconnectHandler(handler: () => void): void {
    this.disconnectHandler = handler;
  }

  isListening(): boolean {
    return this.state !== ServerState.Stopped;
  }

  async start(deviceName: string): Promise<boolean> {
    await this.stop();
    this.deviceName = deviceName;
    this.lastError = '';
    this.clientIp = '';
    this.localIp = this.detectLocalIp();

    try {
      const server: socket.TCPSocketServer = socket.constructTCPSocketServerInstance();
      server.on('connect', (conn: socket.TCPSocketConnection) => {
        this.onClientConnected(conn);
      });
      await server.listen({ address: '0.0.0.0', port: CONTROL_PORT, family: 1 });
      this.tcpServer = server;
      Logger.log('server', `tcp listening 0.0.0.0:${CONTROL_PORT}`);
    } catch (err) {
      this.lastError = `启动失败：端口 ${CONTROL_PORT} 被占用或其他应用抢占，请稍后重试`;
      Logger.log('server', `tcp listen failed: ${err}`);
      this.state = ServerState.Stopped;
      this.stateHandler();
      return false;
    }

    await this.startDiscovery();
    this.state = ServerState.Listening;
    this.stateHandler();
    return true;
  }

  private async startDiscovery(): Promise<void> {
    try {
      const udp: socket.UDPSocket = socket.constructUDPSocketInstance();
      await udp.bind({ address: '0.0.0.0', port: 0, family: 1 });
      await udp.setExtraOptions({ broadcast: true });
      this.disc = udp;
      this.broadcastOnce();
      this.discTimer = setInterval(() => {
        this.broadcastOnce();
      }, 2000);
      Logger.log('server', `discovery broadcast started (udp/${DISCOVERY_PORT})`);
    } catch (err) {
      Logger.log('server', `discovery start failed: ${err}`);
    }
  }

  private broadcastOnce(): void {
    const udp: socket.UDPSocket | undefined = this.disc;
    if (udp === undefined) {
      return;
    }
    const payload: Uint8Array = buildDiscoveryDatagram(this.deviceName);
    const buf: ArrayBuffer = new ArrayBuffer(payload.length);
    new Uint8Array(buf).set(payload, 0);
    this.sendDiscovery(udp, buf, '255.255.255.255');
    const directed: string = this.subnetBroadcast();
    if (directed.length > 0 && directed !== '255.255.255.255') {
      this.sendDiscovery(udp, buf, directed);
    }
  }

  private sendDiscovery(udp: socket.UDPSocket, buf: ArrayBuffer, address: string): void {
    udp.send({
      data: buf,
      address: { address: address, port: DISCOVERY_PORT, family: 1 }
    }).catch((err: Object) => {
      Logger.log('server', `discovery send to ${address} failed: ${err}`);
    });
  }

  /** 取本机第一个 IPv4，同时记下它的前缀长度（掩码位数） */
  private detectLocalIp(): string {
    try {
      const net: connection.NetHandle = connection.getDefaultNetSync();
      if (net === undefined || net.netId === 0) {
        return '';
      }
      const props: connection.ConnectionProperties = connection.getConnectionPropertiesSync(net);
      for (let i = 0; i < props.linkAddresses.length; i++) {
        const link: connection.LinkAddress = props.linkAddresses[i];
        const addr: string = link.address.address;
        if (addr.indexOf('.') > 0 && addr.indexOf(':') < 0) {
          const prefix: number = link.prefixLength;
          if (prefix > 0 && prefix <= 32) {
            this.localPrefixLength = prefix;
          }
          return addr;
        }
      }
    } catch (err) {
      Logger.log('server', `detect local ip failed: ${err}`);
    }
    return '';
  }

  /**
   * 按本机 IP + 真实掩码算出子网定向广播地址。
   * 不能假设 /24 —— 路由器可能是 /16、/22 等，算错就等于没发广播。
   */
  private subnetBroadcast(): string {
    const addr: string = this.localIp.length > 0 ? this.localIp : this.detectLocalIp();
    if (addr.length === 0) {
      return '';
    }
    const parts: string[] = addr.split('.');
    if (parts.length !== 4) {
      return '';
    }

    const o0: number = Number.parseInt(parts[0], 10);
    const o1: number = Number.parseInt(parts[1], 10);
    const o2: number = Number.parseInt(parts[2], 10);
    const o3: number = Number.parseInt(parts[3], 10);
    if (Number.isNaN(o0) || Number.isNaN(o1) || Number.isNaN(o2) || Number.isNaN(o3)) {
      return '';
    }

    const prefix: number = this.localPrefixLength > 0 && this.localPrefixLength <= 32
      ? this.localPrefixLength : 24;
    // /31 是点对点链路，/32 是单机，都没有广播地址
    if (prefix >= 31) {
      return '';
    }

    const ip: number = ((o0 << 24) | (o1 << 16) | (o2 << 8) | o3) >>> 0;
    const mask: number = prefix === 0 ? 0 : ((0xFFFFFFFF << (32 - prefix)) >>> 0);
    const bcast: number = ((ip & mask) | (~mask >>> 0)) >>> 0;

    return `${(bcast >>> 24) & 0xFF}.${(bcast >>> 16) & 0xFF}.${(bcast >>> 8) & 0xFF}.${bcast & 0xFF}`;
  }

  private onClientConnected(conn: socket.TCPSocketConnection): void {
    Logger.log('server', `client connected (clientId=${conn.clientId})`);
    this.conn = conn;
    this.rx = new Uint8Array(0);
    this.handshakeStep = 0;
    conn.on('message', (info: socket.SocketMessageInfo) => {
      this.onData(new Uint8Array(info.message));
    });
    conn.on('close', () => {
      Logger.log('server', 'client disconnected');
      this.stopPing();
      this.conn = undefined;
      const hadClient: boolean = this.clientIp.length > 0;
      this.clientIp = '';
      this.state = ServerState.Listening;
      this.stateHandler();
      if (hadClient) {
        this.disconnectHandler();
      }
    });
    // getRemoteAddress 只用来拿电脑 IP 显示和推流目标。
    // 它一旦失败，原来的写法会导致：握手照常成功（PC 显示已连接），
    // 但 clientHandler 不触发 → 采集不启动 → PC 端一个音频包都收不到，
    // 而手机 UI 还停在「等待电脑连接」。所以要重试，并在最终失败时明确报错。
    const attachClient = (ip: string): void => {
      this.clientIp = ip;
      this.state = ServerState.ClientConnected;
      this.stateHandler();
      this.clientHandler(ip);
    };

    conn.getRemoteAddress().then((addr: socket.NetAddress) => {
      Logger.log('server', `client ip = ${addr.address}`);
      attachClient(addr.address);
    }).catch((err: Object) => {
      Logger.log('server', `getRemoteAddress failed: ${err}, retry after 200ms`);
      setTimeout(() => {
        conn.getRemoteAddress().then((addr2: socket.NetAddress) => {
          Logger.log('server', `client ip (retry) = ${addr2.address}`);
          attachClient(addr2.address);
        }).catch((err2: Object) => {
          Logger.log('server', `getRemoteAddress retry failed: ${err2}`);
          this.lastError = '拿不到电脑端地址，请在电脑上重连一次';
          this.state = ServerState.Listening;
          this.stateHandler();
        });
      }, 200);
    });
    // 服务端先下发 ProtoServerConfig
    this.sendToClient(buildServerConfig(this.deviceName));
    this.handshakeStep = 1;
  }

  private onData(chunk: Uint8Array): void {
    this.rx = concatBytes(this.rx, chunk);
    while (this.rx.length > 0) {
      const parsed: DelimitedResult | undefined = tryParseDelimited(this.rx);
      if (parsed === undefined || parsed.consumed <= 0) {
        break;
      }
      this.rx = this.rx.subarray(parsed.consumed);
      this.onFrame(parsed.frame);
    }
  }

  private onFrame(frame: Uint8Array): void {
    if (this.handshakeStep === 1) {
      this.handshakeStep = 2;
      Logger.log('server', 'ProtoClientConfig received, replying ProtoFeatures');
      this.sendToClient(buildServerFeatures());
      this.state = ServerState.ClientConnected;
      this.startPing();
      this.stateHandler();
      return;
    }
    const msg: ParsedMessage = parseMessage(frame);
    this.inMsgCount = this.inMsgCount + 1;
    if (this.inMsgCount <= 20 || this.inMsgCount % 50 === 0) {
      Logger.log('server', `rx msg kind=${msg.kind} value=${msg.numberValue} text=${msg.textValue} (#${this.inMsgCount}) raw=${toHex(frame, 24)}`);
    }
    if (msg.kind === 'ping_request') {
      this.sendToClient(buildPingResponse(msg.numberValue));
    } else if (msg.kind === 'server_audio_config_request') {
      this.sendToClient(buildServerAudioConfigResponse());
    } else if (msg.kind === 'retransmission_request') {
      this.retransRequests = this.retransRequests + 1;
      const data: Uint8Array | undefined = this.senderRef !== undefined ? this.senderRef.findStreamData(msg.numberValue) : undefined;
      if (data !== undefined) {
        this.sendToClient(buildRetransmissionResponse(data));
      } else {
        this.sendToClient(buildNotFoundRetransmission(msg.numberValue));
      }
    } else if (msg.kind === 'stop') {
      Logger.log('server', `client stop: ${msg.textValue}`);
    }
  }

  sendToClient(message: Uint8Array): void {
    const conn: socket.TCPSocketConnection | undefined = this.conn;
    if (conn === undefined) {
      return;
    }
    const frame: Uint8Array = encodeDelimited(message);
    const buf: ArrayBuffer = new ArrayBuffer(frame.length);
    new Uint8Array(buf).set(frame, 0);
    conn.send({ data: buf }).catch((err: Object) => {
      Logger.log('server', `send failed: ${err}`);
    });
  }

  private startPing(): void {
    this.stopPing();
    this.pingTimer = setInterval(() => {
      if (this.conn === undefined) {
        return;
      }
      this.sendToClient(buildPingRequest(Date.now()));
    }, 2000);
    Logger.log('server', 'heartbeat started (ping every 2s)');
  }

  private stopPing(): void {
    if (this.pingTimer !== -1) {
      clearInterval(this.pingTimer);
      this.pingTimer = -1;
    }
  }

  async stop(): Promise<void> {
    this.stopPing();
    if (this.discTimer !== -1) {
      clearInterval(this.discTimer);
      this.discTimer = -1;
    }
    const disc: socket.UDPSocket | undefined = this.disc;
    this.disc = undefined;
    if (disc !== undefined) {
      try {
        await disc.close();
      } catch (err) {
        Logger.log('server', `discovery close failed: ${err}`);
      }
    }
    const conn: socket.TCPSocketConnection | undefined = this.conn;
    this.conn = undefined;
    if (conn !== undefined) {
      try {
        await conn.close();
      } catch (err) {
        Logger.log('server', `conn close failed: ${err}`);
      }
    }
    const server: socket.TCPSocketServer | undefined = this.tcpServer;
    this.tcpServer = undefined;
    if (server !== undefined) {
      try {
        server.off('connect');
        await server.close();
      } catch (err) {
        Logger.log('server', `tcp server close failed: ${err}`);
      }
    }
    this.state = ServerState.Stopped;
    this.clientIp = '';
    this.stateHandler();
  }
}
