/**
 * 线缆协议消息定义。字段号沿用 AudioRelay 0.27.5 的 schema（从本机 Model.dll 反射核对）。
 * 手机端与接收端都在本仓库内，沿用这套格式只是省掉重新联调的成本；
 * 官方 AudioRelay 桌面端能握手但不解码我们推的流，实际不可用。
 * 端口：TCP 控制 59100 / UDP 音频 59100 / UDP 发现广播 59200。
 */
import { PbReader, PbWriter, WIRE_LEN, WIRE_VARINT } from './Pb';

export const CONTROL_PORT: number = 59100;
export const AUDIO_PORT: number = 59100;
export const DISCOVERY_PORT: number = 59200;

/** ProtoOperatingSystem 枚举里没有 HARMONY 值，上报 ANDROID(5) 兼容性最好 */
export const OS_ANDROID: number = 5;

export const CLIENT_NAME: string = 'MicCast (HarmonyOS)';
export const CLIENT_VERSION: string = '1.0.0';
/** 作为服务端时对外声称的版本，桌面端要求 >= 0.7.0 */
export const SERVER_VERSION: string = '0.27.5';

export const SAMPLE_RATE: number = 48000;
export const BITS: number = 16;
export const CHANNELS: number = 2;
/**
 * 帧长 5ms：48000 * 0.005 * 2ch * 2byte = 960 字节。
 * 必须让整个 UDP 数据报（含 protobuf 头 ~985 字节）小于 MTU 1472，
 * 否则会被 IP 分片，任一分片丢失整帧就废了。
 */
export const FRAME_BYTES: number = 960;
export const FRAME_MS: number = 5;

export class ServerInfo {
  name: string = '';
  version: string = '';
  osVersion: string = '';
  os: number = 0;

  describe(): string {
    return `${this.name} v${this.version}`;
  }
}

export class FeatureSet {
  multiPayload: boolean = false;
  multiPayloadMaxSize: number = 0;
  streamData: boolean = false;
  compression: boolean = false;
  compressionBitrate: number = 0;
  messages: boolean = false;
  retransmission: boolean = false;
  serverAudioConfig: boolean = false;

  summary(): string {
    const items: string[] = [];
    items.push(this.streamData ? 'streamData' : 'raw');
    items.push(this.compression ? `opus@${this.compressionBitrate}` : 'pcm');
    if (this.messages) {
      items.push('messages');
    }
    if (this.retransmission) {
      items.push('retransmit');
    }
    if (this.multiPayload) {
      items.push(`multiPayload(${this.multiPayloadMaxSize})`);
    }
    return items.join(' + ');
  }
}

export class ParsedMessage {
  kind: string = 'unknown';
  numberValue: number = 0;
  textValue: string = '';
  streamData: Uint8Array | undefined = undefined;
  sampleRate: number = 0;
  bits: number = 0;
  channels: number = 0;
}

export function buildClientConfig(): Uint8Array {
  const features: PbWriter = new PbWriter();
  // multi_payload: 不支持（false 也要显式下发，服务端据此决定是否分片）
  const multi: PbWriter = new PbWriter();
  multi.writeBool(1, false);
  features.writeMessage(1, multi.finish());
  // stream_data: 必须开启，UDP 上承载 StreamData 帧
  const streamData: PbWriter = new PbWriter();
  streamData.writeBool(1, true);
  features.writeMessage(2, streamData.finish());
  // compression: 纯 PCM，不压缩
  const compression: PbWriter = new PbWriter();
  compression.writeBool(1, false);
  compression.writeVarint(2, 0);
  features.writeMessage(3, compression.finish());
  // messages: 控制消息信封通道
  const messages: PbWriter = new PbWriter();
  messages.writeBool(1, true);
  features.writeMessage(4, messages.finish());
  // retransmission: 开启，便于服务质量
  const retransmit: PbWriter = new PbWriter();
  retransmit.writeBool(1, true);
  features.writeMessage(5, retransmit.finish());
  // server_audio_config: 允许服务端下发音频参数
  const serverAudioConfig: PbWriter = new PbWriter();
  serverAudioConfig.writeBool(1, true);
  features.writeMessage(7, serverAudioConfig.finish());

  const props: PbWriter = new PbWriter();
  props.writeString(1, CLIENT_NAME);
  props.writeBool(2, false);

  const w: PbWriter = new PbWriter();
  w.writeString(1, CLIENT_NAME);
  w.writeString(3, CLIENT_VERSION);
  w.writeVarint(6, SAMPLE_RATE);
  w.writeVarint(7, BITS);
  w.writeVarint(8, CHANNELS);
  w.writeMessage(9, features.finish());
  w.writeBool(10, false);
  w.writeVarint(13, SAMPLE_RATE);
  w.writeVarint(14, BITS);
  w.writeVarint(15, CHANNELS);
  w.writeString(16, 'HarmonyOS 6.0');
  w.writeVarint(17, OS_ANDROID);
  w.writeMessage(18, props.finish());
  return w.finish();
}

export function parseServerConfig(bytes: Uint8Array): ServerInfo {
  const info: ServerInfo = new ServerInfo();
  const r: PbReader = new PbReader(bytes);
  while (!r.atEnd()) {
    const tag: number = r.readTag();
    const field: number = Math.floor(tag / 8);
    const wire: number = tag & 7;
    if (field === 1 && wire === WIRE_LEN) {
      info.name = r.readString();
    } else if (field === 2 && wire === WIRE_LEN) {
      info.version = r.readString();
    } else if (field === 3 && wire === WIRE_LEN) {
      info.osVersion = r.readString();
    } else if (field === 5 && wire === WIRE_VARINT) {
      info.os = r.readVarint();
    } else if (field === 7 && wire === WIRE_LEN) {
      const sub: PbReader = new PbReader(r.readLengthDelimited());
      while (!sub.atEnd()) {
        const t2: number = sub.readTag();
        if (Math.floor(t2 / 8) === 1 && (t2 & 7) === WIRE_LEN) {
          const propName: string = sub.readString();
          if (propName.length > 0 && info.name.length === 0) {
            info.name = propName;
          }
        } else {
          sub.skip(t2 & 7);
        }
      }
    } else {
      r.skip(wire);
    }
  }
  return info;
}

function parseSwitch(sub: Uint8Array): boolean {
  const r: PbReader = new PbReader(sub);
  let activated: boolean = false;
  while (!r.atEnd()) {
    const tag: number = r.readTag();
    const field: number = Math.floor(tag / 8);
    const wire: number = tag & 7;
    if (field === 1 && wire === WIRE_VARINT) {
      activated = r.readVarint() !== 0;
    } else {
      r.skip(wire);
    }
  }
  return activated;
}

export function parseFeatures(bytes: Uint8Array): FeatureSet {
  const fs: FeatureSet = new FeatureSet();
  const r: PbReader = new PbReader(bytes);
  while (!r.atEnd()) {
    const tag: number = r.readTag();
    const field: number = Math.floor(tag / 8);
    const wire: number = tag & 7;
    if (wire !== WIRE_LEN) {
      r.skip(wire);
      continue;
    }
    const sub: Uint8Array = r.readLengthDelimited();
    if (field === 1) {
      fs.multiPayload = parseSwitch(sub);
      const mr: PbReader = new PbReader(sub);
      while (!mr.atEnd()) {
        const t2: number = mr.readTag();
        const f2: number = Math.floor(t2 / 8);
        if (f2 === 2 && (t2 & 7) === WIRE_VARINT) {
          fs.multiPayloadMaxSize = mr.readVarint();
        } else {
          mr.skip(t2 & 7);
        }
      }
    } else if (field === 2) {
      fs.streamData = parseSwitch(sub);
    } else if (field === 3) {
      fs.compression = parseSwitch(sub);
      const cr: PbReader = new PbReader(sub);
      while (!cr.atEnd()) {
        const t2: number = cr.readTag();
        const f2: number = Math.floor(t2 / 8);
        if (f2 === 2 && (t2 & 7) === WIRE_VARINT) {
          fs.compressionBitrate = cr.readVarint();
        } else {
          cr.skip(t2 & 7);
        }
      }
    } else if (field === 4) {
      fs.messages = parseSwitch(sub);
    } else if (field === 5) {
      fs.retransmission = parseSwitch(sub);
    } else if (field === 7) {
      fs.serverAudioConfig = parseSwitch(sub);
    }
  }
  return fs;
}

export function buildStreamData(tick: number, timestampMs: number, payload: Uint8Array): Uint8Array {
  const w: PbWriter = new PbWriter();
  w.writeVarint(1, tick);
  w.writeVarint(2, timestampMs);
  w.writeBytes(3, payload);
  w.writeVarint(4, payload.length);
  return w.finish();
}

export function buildPingResponse(requestTimestamp: number): Uint8Array {
  const inner: PbWriter = new PbWriter();
  inner.writeVarint(1, requestTimestamp);
  const w: PbWriter = new PbWriter();
  w.writeMessage(22, inner.finish());
  return w.finish();
}

/** 服务端主动心跳：桌面端读消息有 10s 超时，必须周期发送 */
export function buildPingRequest(timestamp: number): Uint8Array {
  const inner: PbWriter = new PbWriter();
  inner.writeVarint(1, timestamp);
  const w: PbWriter = new PbWriter();
  w.writeMessage(21, inner.finish());
  return w.finish();
}

export function buildRetransmissionRequest(tick: number): Uint8Array {
  const inner: PbWriter = new PbWriter();
  inner.writeVarint(1, tick);
  const w: PbWriter = new PbWriter();
  w.writeMessage(9, inner.finish());
  return w.finish();
}

export function buildRetransmissionResponse(streamData: Uint8Array): Uint8Array {
  const inner: PbWriter = new PbWriter();
  inner.writeMessage(1, streamData);
  const w: PbWriter = new PbWriter();
  w.writeMessage(10, inner.finish());
  return w.finish();
}

export function buildNotFoundRetransmission(tick: number): Uint8Array {
  const inner: PbWriter = new PbWriter();
  inner.writeVarint(1, tick);
  const w: PbWriter = new PbWriter();
  w.writeMessage(11, inner.finish());
  return w.finish();
}

export function buildServerAudioConfigRequest(): Uint8Array {
  const w: PbWriter = new PbWriter();
  w.writeMessage(23, new PbWriter().finish());
  return w.finish();
}

// ---------- 服务端（手机做 Server，PC 端播放器连上来当麦克风输入）----------

export function buildServerConfig(deviceName: string): Uint8Array {
  const props: PbWriter = new PbWriter();
  props.writeString(1, deviceName);
  props.writeBool(2, false);
  const w: PbWriter = new PbWriter();
  w.writeString(1, deviceName);
  w.writeString(2, SERVER_VERSION);
  w.writeString(3, 'HarmonyOS 7.0');
  w.writeVarint(5, OS_ANDROID);
  w.writeMessage(7, props.finish());
  return w.finish();
}

export function buildServerFeatures(): Uint8Array {
  const w: PbWriter = new PbWriter();
  const multi: PbWriter = new PbWriter();
  multi.writeBool(1, false);
  w.writeMessage(1, multi.finish());
  const streamData: PbWriter = new PbWriter();
  streamData.writeBool(1, true);
  w.writeMessage(2, streamData.finish());
  const compression: PbWriter = new PbWriter();
  compression.writeBool(1, false);
  compression.writeVarint(2, 0);
  w.writeMessage(3, compression.finish());
  const messages: PbWriter = new PbWriter();
  messages.writeBool(1, true);
  w.writeMessage(4, messages.finish());
  const retransmit: PbWriter = new PbWriter();
  retransmit.writeBool(1, true);
  w.writeMessage(5, retransmit.finish());
  const serverAudioConfig: PbWriter = new PbWriter();
  serverAudioConfig.writeBool(1, true);
  w.writeMessage(7, serverAudioConfig.finish());
  return w.finish();
}

export function buildServerAudioConfigResponse(): Uint8Array {
  const conf: PbWriter = new PbWriter();
  conf.writeVarint(1, SAMPLE_RATE);
  conf.writeVarint(2, BITS);
  conf.writeVarint(3, CHANNELS);
  const inner: PbWriter = new PbWriter();
  inner.writeMessage(1, conf.finish());
  const w: PbWriter = new PbWriter();
  w.writeMessage(24, inner.finish());
  return w.finish();
}

/** 发现广播：int32 LE 长度 + ProtoServerConfig（桌面端按此格式解析） */
export function buildDiscoveryDatagram(deviceName: string): Uint8Array {
  const cfg: Uint8Array = buildServerConfig(deviceName);
  const out: Uint8Array = new Uint8Array(4 + cfg.length);
  out[0] = cfg.length & 0xFF;
  out[1] = (cfg.length >> 8) & 0xFF;
  out[2] = (cfg.length >> 16) & 0xFF;
  out[3] = (cfg.length >> 24) & 0xFF;
  out.set(cfg, 4);
  return out;
}

export function parseMessage(bytes: Uint8Array): ParsedMessage {
  const out: ParsedMessage = new ParsedMessage();
  const r: PbReader = new PbReader(bytes);
  while (!r.atEnd()) {
    const tag: number = r.readTag();
    const field: number = Math.floor(tag / 8);
    const wire: number = tag & 7;
    if (wire !== WIRE_LEN) {
      r.skip(wire);
      continue;
    }
    const sub: Uint8Array = r.readLengthDelimited();
    if (field === 1) {
      out.kind = 'disconnect';
      out.numberValue = readFirstVarint(sub);
    } else if (field === 9) {
      out.kind = 'retransmission_request';
      out.numberValue = readFirstVarint(sub);
    } else if (field === 10) {
      out.kind = 'retransmission_response';
      out.streamData = readFirstBytes(sub);
    } else if (field === 11) {
      out.kind = 'retransmission_not_found';
      out.numberValue = readFirstVarint(sub);
    } else if (field === 12) {
      out.kind = 'stop';
      out.textValue = readFirstString(sub);
    } else if (field === 21) {
      out.kind = 'ping_request';
      out.numberValue = readFirstVarint(sub);
    } else if (field === 22) {
      out.kind = 'ping_response';
      out.numberValue = readFirstVarint(sub);
    } else if (field === 23) {
      out.kind = 'server_audio_config_request';
    } else if (field === 24) {
      out.kind = 'server_audio_config_response';
      const cr: PbReader = new PbReader(sub);
      while (!cr.atEnd()) {
        const t2: number = cr.readTag();
        const f2: number = Math.floor(t2 / 8);
        if (f2 === 1 && (t2 & 7) === WIRE_LEN) {
          const conf: PbReader = new PbReader(cr.readLengthDelimited());
          while (!conf.atEnd()) {
            const t3: number = conf.readTag();
            const f3: number = Math.floor(t3 / 8);
            if ((t3 & 7) === WIRE_VARINT) {
              const v: number = conf.readVarint();
              if (f3 === 1) {
                out.sampleRate = v;
              } else if (f3 === 2) {
                out.bits = v;
              } else if (f3 === 3) {
                out.channels = v;
              }
            } else {
              conf.skip(t3 & 7);
            }
          }
        } else {
          cr.skip(t2 & 7);
        }
      }
    } else if (field === 25) {
      out.kind = 'runtime_properties';
    } else {
      out.kind = 'other';
      out.numberValue = field;
    }
  }
  return out;
}

function readFirstVarint(bytes: Uint8Array): number {
  const r: PbReader = new PbReader(bytes);
  while (!r.atEnd()) {
    const tag: number = r.readTag();
    const field: number = Math.floor(tag / 8);
    const wire: number = tag & 7;
    if (wire === WIRE_VARINT) {
      const v: number = r.readVarint();
      if (field === 1) {
        return v;
      }
    } else {
      r.skip(wire);
    }
  }
  return 0;
}

function readFirstBytes(bytes: Uint8Array): Uint8Array | undefined {
  const r: PbReader = new PbReader(bytes);
  while (!r.atEnd()) {
    const tag: number = r.readTag();
    const field: number = Math.floor(tag / 8);
    const wire: number = tag & 7;
    if (field === 1 && wire === WIRE_LEN) {
      return r.readLengthDelimited();
    }
    r.skip(wire);
  }
  return undefined;
}

function readFirstString(bytes: Uint8Array): string {
  const r: PbReader = new PbReader(bytes);
  while (!r.atEnd()) {
    const tag: number = r.readTag();
    const field: number = Math.floor(tag / 8);
    const wire: number = tag & 7;
    if (field === 1 && wire === WIRE_LEN) {
      return r.readString();
    }
    r.skip(wire);
  }
  return '';
}
