import { audio } from '@kit.AudioKit';
import { socket } from '@kit.NetworkKit';
import { AUDIO_PORT, BITS, CHANNELS, FRAME_BYTES, SAMPLE_RATE, buildStreamData } from '../model/Messages';
import { Logger } from './Logger';

/**
 * 采集模式。系统对不同 SourceType 会套用不同的音频处理链：
 *   Raw         —— UNPROCESSED，原始麦克风数据，最保真、零额外延迟
 *   VoiceRecog  —— 语音识别链，强调清晰度（部分机型与 Raw 相同）
 * 具体效果由厂商 DSP 实现，不同机型差异较大，所以交给用户在界面上自选。
 *
 * 曾经的第三档「语音增强」（SOURCE_TYPE_VOICE_COMMUNICATION）已移除：
 * 那是 VoIP 专用源类型，未建立通话音频场景时会持续返回全零数据，
 * 表现为「采集器在跑、帧率正常、但麦克风输入一直静音」，无法可靠使用。
 */
export enum MicMode {
  Raw = 0,
  VoiceRecog = 1
}

export function micModeLabel(mode: MicMode): string {
  if (mode === MicMode.Raw) {
    return '原始';
  }
  return '识别优先';
}

function toSourceType(mode: MicMode): audio.SourceType {
  if (mode === MicMode.Raw) {
    // UNPROCESSED 是官方给「原始数据采集」的类型（API 14+）。
    // 比 MIC 更贴近「不做任何处理」，MIC 仍可能过一道基础优化。
    return audio.SourceType.SOURCE_TYPE_UNPROCESSED;
  }
  return audio.SourceType.SOURCE_TYPE_VOICE_RECOGNITION;
}

/**
 * 采集麦克风 PCM（16kHz/16bit/单声道）→ 每 5ms 打成 StreamData protobuf → UDP 发往 PC:59100。
 * 同时保留最近 100 帧，供服务端发起丢包重传时回填。
 */
export class AudioSender {
  private capturer: audio.AudioCapturer | undefined = undefined;
  private udp: socket.UDPSocket | undefined = undefined;
  private targetIp: string = '';
  private pending: number[] = [];
  private monoInput: boolean = false;
  private tick: number = 0;
  private ringTicks: number[] = [];
  private ringData: Uint8Array[] = [];
  private startedAt: number = 0;
  private running: boolean = false;
  // 默认「原始」：零处理、零额外延迟，最少意外。
  private mode: MicMode = MicMode.Raw;

  framesSent: number = 0;
  bytesSent: number = 0;
  capturing: boolean = false;
  readCount: number = 0;
  errCount: number = 0;
  sentOk: number = 0;
  sendErr: number = 0;
  rxFromServer: number = 0;
  peerPort: number = 0;
  targetPort: number = AUDIO_PORT;
  /** 实时输入电平 0..1（已做衰减式平滑），供 UI 显示麦克风是否在收音 */
  level: number = 0;

  private static computeRms(payload: Uint8Array): number {
    let sum: number = 0;
    let count: number = 0;
    for (let i = 0; i + 1 < payload.length; i += 2) {
      let value: number = payload[i] | (payload[i + 1] << 8);
      if (value > 32767) {
        value = value - 65536;
      }
      const sample: number = value / 32768;
      sum += sample * sample;
      count++;
    }
    return count > 0 ? Math.sqrt(sum / count) : 0;
  }

  isRunning(): boolean {
    return this.running;
  }

  uptimeMs(): number {
    if (!this.running) {
      return 0;
    }
    return Date.now() - this.startedAt;
  }

  /** 当前采集模式 */
  currentMode(): MicMode {
    return this.mode;
  }

  /**
   * 切换采集模式。
   * 只重建采集器，**UDP 通道保持不变** —— 否则重建 socket 时会重新 bind 59100，
   * 旧 socket 尚未完全释放就会冲突，导致推流卡死。
   */
  async setMode(mode: MicMode): Promise<void> {
    if (this.mode === mode) {
      return;
    }
    const previous = this.mode;
    this.mode = mode;
    if (!this.running) {
      return;
    }
    try {
      await this.stopCapturer();
      await this.startCapturer();
      Logger.log('audio', `mic mode now ${micModeLabel(mode)}`);
    } catch (err) {
      this.mode = previous;
      Logger.log('audio', `switch mic mode failed: ${err}`);
      throw err as Error;
    }
  }

  /** 创建并启动采集器，把数据接到 onPcm。 */
  private async startCapturer(): Promise<void> {
    const capturer: audio.AudioCapturer = await this.createCapturer();
    this.capturer = capturer;
    capturer.on('readData', (buffer: ArrayBuffer) => {
      this.readCount = this.readCount + 1;
      if (this.readCount <= 3 || this.readCount % 500 === 0) {
        Logger.log('audio', `readData #${this.readCount} bytes=${buffer.byteLength}`);
      }
      try {
        this.onPcm(new Uint8Array(buffer));
      } catch (err) {
        this.errCount = this.errCount + 1;
        if (this.errCount <= 3) {
          Logger.log('audio', `onPcm ERROR: ${err}`);
        }
      }
    });
    await capturer.start();
    this.capturing = true;
    this.running = true;
    if (this.startedAt === 0) {
      this.startedAt = Date.now();
    }
    let bufSize: number = -1;
    try {
      bufSize = await capturer.getBufferSize();
    } catch (err) {
      Logger.log('audio', `getBufferSize failed: ${err}`);
    }
    Logger.log('audio', `capture started (16kHz mono, ${micModeLabel(this.mode)}) bufSize=${bufSize}`);
  }

  /** 停止并释放采集器，不动 UDP。 */
  private async stopCapturer(): Promise<void> {
    const capturer: audio.AudioCapturer | undefined = this.capturer;
    this.capturer = undefined;
    this.capturing = false;
    if (capturer !== undefined) {
      try {
        capturer.off('readData');
        await capturer.stop();
        await capturer.release();
      } catch (err) {
        Logger.log('audio', `capturer stop failed: ${err}`);
      }
    }
    // release 是异步的，稍等一下让音频框架彻底回收，否则立刻重建会失败
    await new Promise<void>((resolve) => setTimeout(() => resolve(), 120));
  }

  async start(ip: string, port: number = AUDIO_PORT): Promise<void> {
    await this.stop();
    // close() 是异步的，立刻重绑同一端口可能失败
    await new Promise<void>((resolve) => setTimeout(() => resolve(), 150));
    this.targetIp = ip;
    this.targetPort = port;
    this.pending = [];
    this.tick = 0;
    this.ringTicks = [];
    this.ringData = [];
    this.framesSent = 0;
    this.bytesSent = 0;
    this.level = 0;
    // 每次完整启动都重置「已串流」计时；否则停止再启动会显示累计时间，看着像没停过。
    // 切换采集模式走的是 setMode→startCapturer，不经过这里，所以不会误重置。
    this.startedAt = 0;

    // 端口 59100 是复用的：上一次连接刚关，系统可能还没彻底释放。
    // 之前只等 150ms 就 bind，偶尔失败会直接抛异常，表现为「PC 显示已连接但一个音频包都收不到」。
    // 这里改成带退避的重试。
    let udp: socket.UDPSocket | undefined = undefined;
    let bindError: Object | undefined = undefined;
    for (let attempt = 1; attempt <= 4; attempt++) {
      const candidate: socket.UDPSocket = socket.constructUDPSocketInstance();
      try {
        // 官方实现「双方各绑本地 59100」：服务端也要从 59100 发出，客户端按源端口过滤
        await candidate.bind({ address: '0.0.0.0', port: AUDIO_PORT, family: 1 });
        udp = candidate;
        if (attempt > 1) {
          Logger.log('audio', `udp bind succeeded on attempt ${attempt}`);
        }
        break;
      } catch (err) {
        bindError = err;
        Logger.log('audio', `udp bind attempt ${attempt} failed: ${err}`);
        try { await candidate.close(); } catch (closeErr) { }
        if (attempt < 4) {
          await new Promise<void>((resolve) => setTimeout(() => resolve(), 250 * attempt));
        }
      }
    }
    if (udp === undefined) {
      throw new Error(`UDP 端口 ${AUDIO_PORT} 绑定失败：${bindError}`);
    }
    const sock: socket.UDPSocket = udp;
    sock.on('message', (info: socket.SocketMessageInfo) => {
      this.rxFromServer = this.rxFromServer + 1;
    });
    this.udp = sock;

    await this.startCapturer();
    Logger.log('audio', `streaming to ${ip}:${AUDIO_PORT}`);
  }

  private async createCapturer(): Promise<audio.AudioCapturer> {
    const capturerInfo: audio.AudioCapturerInfo = {
      source: toSourceType(this.mode),
      capturerFlags: 0
    };
    // 固定 16kHz 单声道：麦克风通话够用，带宽只有 48kHz 立体声的 1/6。
    // 不做采样率回退 —— 回退会让实际数据与协议声明不符，对端会变调。
    const streamInfo: audio.AudioStreamInfo = {
      samplingRate: audio.AudioSamplingRate.SAMPLE_RATE_16000,
      channels: audio.AudioChannel.CHANNEL_1,
      sampleFormat: audio.AudioSampleFormat.SAMPLE_FORMAT_S16LE,
      encodingType: audio.AudioEncodingType.ENCODING_TYPE_RAW
    };
    let cap: audio.AudioCapturer;
    try {
      cap = await audio.createAudioCapturer({
        streamInfo: streamInfo,
        capturerInfo: capturerInfo
      });
    } catch (err) {
      // UNPROCESSED / LIVE 依赖设备能力，个别机型可能不支持。
      // 回退到 MIC，宁可少一层处理也不要采不到声音。
      const fallback: audio.AudioCapturerInfo = {
        source: audio.SourceType.SOURCE_TYPE_MIC,
        capturerFlags: 0
      };
      Logger.log('audio', `source ${capturerInfo.source} unsupported (${err}), fallback to MIC`);
      cap = await audio.createAudioCapturer({
        streamInfo: streamInfo,
        capturerInfo: fallback
      });
    }
    this.monoInput = true;
    return cap;
  }

  private onPcm(chunk: Uint8Array): void {
    // 采集就是 16kHz 单声道，与协议声明一致，直接入队即可（不再复制成双声道）
    for (let i = 0; i < chunk.length; i++) {
      this.pending.push(chunk[i]);
    }
    while (this.pending.length >= FRAME_BYTES) {
      const frame: Uint8Array = new Uint8Array(FRAME_BYTES);
      for (let i = 0; i < FRAME_BYTES; i++) {
        frame[i] = this.pending[i];
      }
      this.pending.splice(0, FRAME_BYTES);
      this.emitFrame(frame);
    }
  }

  private emitFrame(payload: Uint8Array): void {
    const tick: number = this.tick;
    this.tick = this.tick + 1;
    this.framesSent = this.framesSent + 1;
    this.bytesSent = this.bytesSent + payload.length;
    const rms: number = AudioSender.computeRms(payload);
    this.level = rms > this.level ? rms : this.level * 0.82;
    const streamData: Uint8Array = buildStreamData(tick, Date.now(), payload);
    this.ringTicks.push(tick);
    this.ringData.push(streamData);
    while (this.ringTicks.length > 100) {
      this.ringTicks.shift();
      this.ringData.shift();
    }
    this.sendUdp(streamData);
    if (this.framesSent % 100 === 0) {
      Logger.log('audio', `frames=${this.framesSent} sentOk=${this.sentOk} sendErr=${this.sendErr} -> ${this.targetIp}:${this.targetPort}`);
    }
  }

  private sendUdp(data: Uint8Array): void {
    const udp: socket.UDPSocket | undefined = this.udp;
    if (udp === undefined) {
      return;
    }
    try {
      const buf: ArrayBuffer = new ArrayBuffer(data.length);
      const view: Uint8Array = new Uint8Array(buf);
      view.set(data, 0);
      udp.send({
        data: buf,
        address: { address: this.targetIp, port: this.targetPort, family: 1 }
      }).then(() => {
        this.sentOk = this.sentOk + 1;
      }).catch((err: Object) => {
        this.sendErr = this.sendErr + 1;
        if (this.sendErr <= 3) {
          Logger.log('audio', `udp send failed: ${err}`);
        }
      });
    } catch (err) {
      this.sendErr = this.sendErr + 1;
      if (this.sendErr <= 3) {
        Logger.log('audio', `udp send THREW: ${err}`);
      }
    }
  }

  /** 重传查找：返回该 tick 对应的 StreamData 字节 */
  findStreamData(tick: number): Uint8Array | undefined {
    for (let i = this.ringTicks.length - 1; i >= 0; i--) {
      if (this.ringTicks[i] === tick) {
        return this.ringData[i];
      }
    }
    return undefined;
  }

  async stop(): Promise<void> {
    this.running = false;
    this.level = 0;
    await this.stopCapturer();
    const udp: socket.UDPSocket | undefined = this.udp;
    this.udp = undefined;
    if (udp !== undefined) {
      try {
        udp.off('message');
        await udp.close();
      } catch (err) {
        Logger.log('audio', `udp close failed: ${err}`);
      }
    }
  }
}
