import { abilityAccessCtrl, common, Permissions } from '@kit.AbilityKit';
import { deviceInfo } from '@kit.BasicServicesKit';
import { AudioSender, MicMode, micModeLabel } from '../service/AudioSender';
import { MicServer } from '../service/MicServer';
import { BackgroundTaskGuard } from '../service/BackgroundTaskGuard';
import { Logger } from '../service/Logger';
import { Tokens } from '../model/Tokens';
import { AUDIO_PORT } from '../model/Messages';

const MIC_MODES: MicMode[] = [MicMode.Raw, MicMode.VoiceRecog];

function pad2(value: number): string {
  return value < 10 ? `0${value}` : `${value}`;
}

/**
 * 服务层提升到**模块级单例**，不再挂在页面 struct 里。
 *
 * 原因：页面销毁（Ability 重建、系统回收 UI）会把 struct 里的成员一起丢掉，
 * 那时的表现就是"服务自己停了"。模块级变量的生命周期跟着 ES 模块/进程走，
 * 页面来去都不影响正在跑的服务。
 *
 * 注意：这**不是**独立进程。鸿蒙的 ServiceExtensionAbility 是系统应用专用的，
 * 三方应用用不了；而且拆进程也不能免于后台冻结 —— 冻结与否看的是有没有
 * 合法的长时任务，不是看在哪个进程。
 */
const micServer: MicServer = new MicServer();
const micSender: AudioSender = new AudioSender();
const micGuard: BackgroundTaskGuard = new BackgroundTaskGuard();

@Entry
@Component
struct Index {
  @State running: boolean = false;
  @State streaming: boolean = false;
  @State clientIp: string = '';
  @State localIp: string = '';
  @State deviceName: string = 'HarmonyOS Phone';
  @State uptime: string = '00:00';
  @State fpsRate: number = 0;
  @State kbps: number = 0;
  @State level: number = 0;
  @State levelPercent: number = 0;
  @State message: string = '';
  @State logs: string[] = [];
  @State showLogs: boolean = false;
  @State showGuide: boolean = true;
  @State micMode: MicMode = MicMode.Raw;

  private server: MicServer = micServer;
  private sender: AudioSender = micSender;
  private bgGuard: BackgroundTaskGuard = micGuard;
  private timer: number = -1;
  private lastBytes: number = 0;
  private streamingIp: string = '';
  private stopping: boolean = false;
  private lastKbpsAt: number = 0;
  private lastFrames: number = 0;

  aboutToAppear(): void {
    try {
      const model: string = deviceInfo.productModel;
      if (model.length > 0) {
        this.deviceName = model;
      }
    } catch (err) {
      Logger.log('ui', `read device model failed: ${err}`);
    }
    this.server.setStateHandler(() => {
      this.refresh();
    });
    this.server.setSender(this.sender);
    this.server.setClientHandler((ip: string) => {
      this.onPcConnected(ip);
    });
    this.server.setDisconnectHandler(() => {
      this.onPcDisconnected();
    });
    this.timer = setInterval(() => {
      this.refresh();
    }, 250);
    this.refresh();
  }

  aboutToDisappear(): void {
    if (this.timer !== -1) {
      clearInterval(this.timer);
      this.timer = -1;
    }
    // **不要在这里停服务。**
    // aboutToDisappear 是页面销毁，不是用户点的「停止」。页面没了服务还得活着，
    // 否则切后台/Ability 重建就把正在跑的麦克风一起带走了。
    // 只有用户明确点「停止」才走 stopAll()。
  }

  private async stopAll(): Promise<void> {
    this.stopping = true;
    const ctx: common.UIAbilityContext = this.getUIContext().getHostContext() as common.UIAbilityContext;
    try {
      await this.sender.stop();
      await this.bgGuard.stop(ctx);
      await this.server.stop();
    } finally {
      this.streamingIp = '';
      this.stopping = false;
    }
    this.refresh();
  }

  /** 电脑端断开：必须停掉采集，否则会一直录着麦克风并显示「正在串流」 */
  private async onPcDisconnected(): Promise<void> {
    if (this.stopping) {
      return;
    }
    // 只停推流，**保持采集**。
    // 采集一停，「AUDIO_RECORDING 长时任务的运行态一致性校验」就过不了，
    // 系统会取消长时任务并冻结进程；之后再重连 sender 起不来，
    // 表现为 PC 端 connected=True 但 fps=0。所以断开期间采集必须继续。
    this.sender.detach();
    this.streamingIp = '';
    this.getUIContext().getPromptAction().showToast({ message: '电脑已断开，麦克风仍在待命' });
    this.refresh();
  }

  private refresh(): void {
    this.running = this.server.isListening();
    // streaming 表示"正在往 PC 推流"，不等于"在采集" —— 服务待命时采集是开着的
    this.streaming = this.sender.isSending();
    this.clientIp = this.server.clientIp;
    if (this.server.localIp.length > 0) {
      this.localIp = this.server.localIp;
    }
    const totalSec: number = Math.floor(this.sender.uptimeMs() / 1000);
    this.uptime = `${pad2(Math.floor(totalSec / 60))}:${pad2(totalSec % 60)}`;
    const deltaBytes: number = this.sender.bytesSent - this.lastBytes;
    this.lastBytes = this.sender.bytesSent;
    const deltaFrames: number = this.sender.framesSent - this.lastFrames;
    this.lastFrames = this.sender.framesSent;
    const now: number = Date.now();
    if (this.lastKbpsAt > 0) {
      const dtSec: number = (now - this.lastKbpsAt) / 1000;
      if (dtSec > 0.05) {
        this.kbps = Math.round(deltaBytes * 8 / dtSec / 1000);
        this.fpsRate = Math.round(deltaFrames / dtSec);
      }
    }
    this.lastKbpsAt = now;

    this.level = this.sender.level;
    const db: number = this.level > 0.0005 ? 20 * Math.log10(this.level) : -60;
    const ratio: number = Math.max(0, Math.min(1, (db + 60) / 60));
    this.levelPercent = Math.round(ratio * 100);

    if (this.server.lastError.length > 0) {
      this.message = this.server.lastError;
    }
    if (this.showLogs) {
      // 倒序：最新的在最上面，不用滚动就能看到刚发生的事
      this.logs = Logger.lines.slice(Math.max(0, Logger.lines.length - 60)).reverse();
    }
  }

  private statusText(): string {
    if (this.streaming) {
      return '正在串流';
    }
    return this.running ? '等待电脑连接' : '未启动';
  }

  private levelLabel(): string {
    // 用「无输入」而不是「静音」：静音是会议软件的功能，这里是"当前没收到声音"
    if (this.level <= 0.0005) {
      return '无输入';
    }
    return `${Math.round(20 * Math.log10(this.level))} dB`;
  }

  private hintText(): string {
    if (this.streaming) {
      return `麦克风正在送往 ${this.clientIp}。电脑上打开微信 / 腾讯会议 / Win+H，麦克风选「CABLE Output」就能说话。`;
    }
    if (this.running) {
      return this.localIp.length > 0
        ? `电脑端 MicCast 会自动发现本机。若没发现，在电脑上手动填这个地址：${this.localIp}`
        : '电脑端 MicCast 会自动发现本机，稍等一下。';
    }
    return '点下面的按钮开始，电脑和手机连同一个 Wi-Fi 就行。';
  }

  private modeHint(): string {
    if (this.micMode === MicMode.Raw) {
      return '原始音频，不做任何处理。最保真，适合安静环境和语音输入。';
    }
    return '语音识别用的处理链，强调清晰度。会有约 10ms 起始延迟，建议优先用「原始」录音。';
  }

  private async ensureMicPermission(): Promise<boolean> {
    try {
      const ctx: common.UIAbilityContext = this.getUIContext().getHostContext() as common.UIAbilityContext;
      const atManager: abilityAccessCtrl.AtManager = abilityAccessCtrl.createAtManager();
      const perms: Permissions[] = ['ohos.permission.MICROPHONE'];
      const result = await atManager.requestPermissionsFromUser(ctx, perms);
      return result.authResults.length > 0 && result.authResults[0] === 0;
    } catch (err) {
      Logger.log('ui', `permission request failed: ${err}`);
      return false;
    }
  }

  private async onPcConnected(ip: string): Promise<void> {
    // 已经在给这台机器推流，什么都不用做
    if (this.sender.isSending() && this.streamingIp === ip) {
      return;
    }

    const ctx: common.UIAbilityContext = this.getUIContext().getHostContext() as common.UIAbilityContext;

    // 兜底：采集没在跑（例如用户是从托盘/别的路径连上来的）就补齐，
    // 顺序仍是「先长时任务、再采集器」。
    if (!this.sender.isRunning()) {
      const granted: boolean = await this.ensureMicPermission();
      if (!granted) {
        this.getUIContext().getPromptAction().showToast({ message: '没有麦克风权限，无法把手机当麦克风用' });
        return;
      }
      await this.bgGuard.start(ctx);
      try {
        await this.sender.setMode(this.micMode);
        await this.sender.start();
      } catch (err) {
        this.message = `麦克风启动失败：${err}`;
        Logger.log('ui', this.message);
        await this.bgGuard.stop(ctx);
        this.refresh();
        return;
      }
    }

    // 采集已经在跑，只需要挂上推流目标 —— 不重建采集器，省掉空窗
    this.sender.attach(ip, AUDIO_PORT);
    this.streamingIp = ip;
    this.getUIContext().getPromptAction().showToast({ message: `已连接电脑 ${ip}，开始串流` });
    Logger.log('ui', `streaming mic to ${ip} (${micModeLabel(this.micMode)})`);
    this.refresh();
  }

  /** 切换采集模式：串流中会重连采集，有极短暂音属正常 */
  private async setMicMode(mode: MicMode): Promise<void> {
    if (this.micMode === mode) {
      return;
    }
    this.micMode = mode;
    if (this.sender.isRunning()) {
      try {
        await this.sender.setMode(mode);
        this.getUIContext().getPromptAction().showToast({ message: `已切换到「${micModeLabel(mode)}」` });
      } catch (err) {
        Logger.log('ui', `switch mode failed: ${err}`);
        // 常驻显示，别让用户错过
        this.message = `切换采集模式失败：${err}`;
        this.getUIContext().getPromptAction().showToast({ message: `切换失败：${err}` });
      }
    }
    this.refresh();
  }

  private async toggleServer(): Promise<void> {
    if (this.server.isListening()) {
      await this.stopAll();
      this.getUIContext().getPromptAction().showToast({ message: '已停止' });
      return;
    }
    this.message = '';
    const ok: boolean = await this.server.start(`${this.deviceName} (MicCast)`);
    if (!ok) {
      this.getUIContext().getPromptAction().showToast({
        message: this.server.lastError.length > 0 ? this.server.lastError : '启动失败'
      });
      this.refresh();
      return;
    }

    // 服务启动就**开始采集**（不是等 PC 连接）。
    // 鸿蒙对 AUDIO_RECORDING 长时任务做运行态一致性校验：声明录音却不录音，
    // 系统会取消长时任务并冻结进程。所以「服务开着」必须等于「正在录音」，
    // 否则切后台就被冻结，PC 重连回来起不来（connected=True 但 fps=0）。
    const ctx: common.UIAbilityContext = this.getUIContext().getHostContext() as common.UIAbilityContext;
    const granted: boolean = await this.ensureMicPermission();
    if (granted) {
      // 顺序按官方建议：先申请长时任务，再启动采集器
      await this.bgGuard.start(ctx);
      try {
        await this.sender.setMode(this.micMode);
        await this.sender.start();
      } catch (err) {
        this.message = `麦克风启动失败：${err}`;
        Logger.log('ui', this.message);
        await this.bgGuard.stop(ctx);
      }
    }
    this.refresh();
  }

  build() {
    Column() {
      // ── 顶部渐变 Hero ─────────────────────────────────────
      Column({ space: Tokens.cardGap }) {
        Row({ space: Tokens.space2 }) {
          Image($r('app.media.brand_mark'))
            .width(Tokens.iconSize)
            .height(Tokens.iconSize)
            .objectFit(ImageFit.Contain)

          Column({ space: Tokens.space1 }) {
            Text('MicCast')
              .fontSize(Tokens.fontHero)
              .fontWeight(FontWeight.Bold)
              .fontColor($r('app.color.on_hero'))
            Text('声渡 · 手机当麦克风')
              .fontSize(Tokens.fontCaption)
              .fontColor($r('app.color.on_hero_soft'))
          }
          .alignItems(HorizontalAlign.Start)

          Blank()

          Text(this.statusText())
            .fontSize(Tokens.fontLabel)
            .fontColor($r('app.color.on_hero'))
            .padding({ left: Tokens.space3, right: Tokens.space3, top: Tokens.space1, bottom: Tokens.space1 })
            .backgroundColor('#33FFFFFF')
            .borderRadius(Tokens.radiusPill)
        }
        .width('100%')
        .alignItems(VerticalAlign.Center)

        if (this.streaming) {
          Column({ space: Tokens.space2 }) {
            Row() {
              Text('麦克风输入')
                .fontSize(Tokens.fontCaption)
                .fontColor($r('app.color.on_hero_soft'))
              Blank()
              Text(this.levelLabel())
                .fontSize(Tokens.fontCaption)
                .fontColor($r('app.color.on_hero_soft'))
            }
            .width('100%')

            Stack({ alignContent: Alignment.Start }) {
              Column()
                .width('100%')
                .height(Tokens.levelBarHeight)
                .borderRadius(3)
                .backgroundColor($r('app.color.hero_track'))
              Column()
                .width(`${this.levelPercent}%`)
                .height(Tokens.levelBarHeight)
                .borderRadius(3)
                .backgroundColor($r('app.color.on_hero'))
                .animation({ duration: 200, curve: Curve.EaseOut })
            }
            .width('100%')
            .height(Tokens.levelBarHeight)

            Divider()
              .color('#33FFFFFF')
              .margin({ top: Tokens.space1 })

            Row() {
              Column({ space: Tokens.space1 }) {
                Text(this.uptime)
                  .fontSize(Tokens.fontMetric)
                  .fontWeight(FontWeight.Bold)
                  .fontColor($r('app.color.on_hero'))
                Text('已串流')
                  .fontSize(Tokens.fontCaption)
                  .fontColor($r('app.color.on_hero_soft'))
              }
              .layoutWeight(1)
              .alignItems(HorizontalAlign.Start)

              Column({ space: Tokens.space1 }) {
                Text(`${this.fpsRate}`)
                  .fontSize(Tokens.fontMetric)
                  .fontWeight(FontWeight.Bold)
                  .fontColor($r('app.color.on_hero'))
                Text('帧/秒')
                  .fontSize(Tokens.fontCaption)
                  .fontColor($r('app.color.on_hero_soft'))
              }
              .layoutWeight(1)
              .alignItems(HorizontalAlign.Center)

              Column({ space: Tokens.space1 }) {
                Text(`${this.kbps}`)
                  .fontSize(Tokens.fontMetric)
                  .fontWeight(FontWeight.Bold)
                  .fontColor($r('app.color.on_hero'))
                Text('kbps')
                  .fontSize(Tokens.fontCaption)
                  .fontColor($r('app.color.on_hero_soft'))
              }
              .layoutWeight(1)
              .alignItems(HorizontalAlign.End)
            }
            .width('100%')
          }
          .width('100%')
          .transition(TransitionEffect.OPACITY.combine(TransitionEffect.translate({ y: -10 })))
        }
      }
      .width(`calc(100% - ${Tokens.pagePadding * 2}vp)`)
      .padding({ left: Tokens.cardPadding, right: Tokens.cardPadding, top: Tokens.cardPadding, bottom: Tokens.cardPadding })
      .linearGradient({
        angle: 135,
        colors: [[$r('app.color.hero_from'), 0.0], [$r('app.color.hero_to'), 1.0]]
      })
      .borderRadius(Tokens.radiusHero)
      .margin({ left: Tokens.pagePadding, right: Tokens.pagePadding, top: Tokens.cardGap })

      Scroll() {
        Column({ space: Tokens.cardGap }) {
          // ── 主操作 ──────────────────────────────────────
          Column({ space: Tokens.space4 }) {
            Text(this.hintText())
              .fontSize(Tokens.fontBody)
              .lineHeight(20)
              .fontColor($r('app.color.text_muted'))
              .width('100%')

            Button(this.running ? '停止' : '启动麦克风服务')
              .width('100%')
              .height(Tokens.buttonHeight)
              .fontSize(Tokens.fontButton)
              .fontWeight(FontWeight.Medium)
              // 停止是可逆的次要动作，用中性底；红色留给真正的破坏性操作
              .backgroundColor(this.running ? $r('app.color.card_bg_alt') : $r('app.color.accent'))
              .fontColor(this.running ? $r('app.color.text_main') : $r('app.color.on_accent'))
              .borderRadius(Tokens.radiusControl)
              .border({
                width: this.running ? 1 : 0,
                color: $r('app.color.divider')
              })
              .animation({ duration: Tokens.durFast, curve: Curve.EaseInOut })
              .stateStyles({
                pressed: {
                  .scale({ x: 0.97, y: 0.97 })
                  .opacity(0.92)
                },
                normal: {
                  .scale({ x: 1, y: 1 })
                  .opacity(1)
                }
              })
              .onClick(() => {
                this.toggleServer();
              })

            if (this.message.length > 0) {
              Text(this.message)
                .fontSize(Tokens.fontLabel)
                .lineHeight(18)
                .fontColor($r('app.color.state_warn'))
                .width('100%')
            }

            Divider()
              .color($r('app.color.divider'))
              .margin({ top: 2 })

            // 采集模式：系统对不同 SourceType 套用不同的音频处理链，效果因机型而异
            Column({ space: Tokens.space2 }) {
              Row() {
                Text('采集模式')
                  .fontSize(Tokens.fontTitle)
                  .fontColor($r('app.color.text_muted'))
                Blank()
                Text(this.streaming ? '切换会让声音断一下' : '')
                  .fontSize(Tokens.fontCaption)
                  .fontColor($r('app.color.text_muted'))
              }
              .width('100%')

              Row({ space: 0 }) {
                ForEach(MIC_MODES, (mode: MicMode) => {
                  Text(micModeLabel(mode))
                    .layoutWeight(1)
                    .height(Tokens.segmentedHeight)
                    .fontSize(Tokens.fontBody)
                    .textAlign(TextAlign.Center)
                    .fontColor(this.micMode === mode ? $r('app.color.on_accent') : $r('app.color.text_muted'))
                    .backgroundColor(this.micMode === mode ? $r('app.color.accent') : Color.Transparent)
                    .borderRadius(Tokens.radiusSmall)
                    .animation({ duration: 180, curve: Curve.EaseOut })
                    .onClick(() => {
                      this.setMicMode(mode);
                    })
                }, (mode: MicMode) => `${mode}`)
              }
              .padding(3)
              .backgroundColor($r('app.color.tile_bg'))
              .borderRadius(Tokens.radiusControl)
              .width('100%')

              Text(this.modeHint())
                .fontSize(Tokens.fontCaption)
                .lineHeight(16)
                .fontColor($r('app.color.text_muted'))
                .width('100%')
            }
            .width('100%')
            .alignItems(HorizontalAlign.Start)
          }
          .width('100%')
          .padding(Tokens.cardPadding)
          .backgroundColor($r('app.color.card_bg'))
          .borderRadius(Tokens.radiusCard)
          .border({ width: 1, color: $r('app.color.divider') })
          .alignItems(HorizontalAlign.Start)

          // ── 电脑端指引 ──────────────────────────────────
          Column({ space: Tokens.cardGap }) {
            Row() {
              Text('电脑上怎么用')
                .fontSize(Tokens.fontTitle)
                .fontWeight(FontWeight.Medium)
                .fontColor($r('app.color.text_main'))
              Blank()
              Text(this.showGuide ? '收起' : '展开')
                .fontSize(Tokens.fontLabel)
                .fontColor($r('app.color.accent'))
                .onClick(() => {
                  this.getUIContext().animateTo({ duration: 220, curve: Curve.EaseInOut }, () => {
                    this.showGuide = !this.showGuide;
                  });
                })
            }
            .width('100%')

            if (this.showGuide) {
              Row({ space: Tokens.space2 }) {
                Text('1')
                  .fontSize(Tokens.fontCaption)
                  .fontColor($r('app.color.accent'))
                  .textAlign(TextAlign.Center)
                  .lineHeight(20)
                  .width(Tokens.space5)
                  .height(Tokens.space5)
                  .backgroundColor($r('app.color.tile_bg'))
                  .borderRadius(Tokens.radiusControl)
                Text('运行电脑上的 MicCastReceiver.exe')
                  .fontSize(Tokens.fontBody)
                  .lineHeight(20)
                  .fontColor($r('app.color.text_muted'))
                  .layoutWeight(1)
              }
              .width('100%')
              .alignItems(VerticalAlign.Top)
              .transition(TransitionEffect.OPACITY)

              Row({ space: Tokens.space2 }) {
                Text('2')
                  .fontSize(Tokens.fontCaption)
                  .fontColor($r('app.color.accent'))
                  .textAlign(TextAlign.Center)
                  .lineHeight(20)
                  .width(Tokens.space5)
                  .height(Tokens.space5)
                  .backgroundColor($r('app.color.tile_bg'))
                  .borderRadius(Tokens.radiusControl)
                Text('设备列表里选中本机，点「开始接收」')
                  .fontSize(Tokens.fontBody)
                  .lineHeight(20)
                  .fontColor($r('app.color.text_muted'))
                  .layoutWeight(1)
              }
              .width('100%')
              .alignItems(VerticalAlign.Top)
              .transition(TransitionEffect.OPACITY)

              Row({ space: Tokens.space2 }) {
                Text('3')
                  .fontSize(Tokens.fontCaption)
                  .fontColor($r('app.color.accent'))
                  .textAlign(TextAlign.Center)
                  .lineHeight(20)
                  .width(Tokens.space5)
                  .height(Tokens.space5)
                  .backgroundColor($r('app.color.tile_bg'))
                  .borderRadius(Tokens.radiusControl)
                Text('输出设备选 CABLE Input')
                  .fontSize(Tokens.fontBody)
                  .lineHeight(20)
                  .fontColor($r('app.color.text_muted'))
                  .layoutWeight(1)
              }
              .width('100%')
              .alignItems(VerticalAlign.Top)
              .transition(TransitionEffect.OPACITY)

              Row({ space: Tokens.space2 }) {
                Text('4')
                  .fontSize(Tokens.fontCaption)
                  .fontColor($r('app.color.accent'))
                  .textAlign(TextAlign.Center)
                  .lineHeight(20)
                  .width(Tokens.space5)
                  .height(Tokens.space5)
                  .backgroundColor($r('app.color.tile_bg'))
                  .borderRadius(Tokens.radiusControl)
                Text('微信 / 腾讯会议 / Win+H 的麦克风选 CABLE Output')
                  .fontSize(Tokens.fontBody)
                  .lineHeight(20)
                  .fontColor($r('app.color.text_muted'))
                  .layoutWeight(1)
              }
              .width('100%')
              .alignItems(VerticalAlign.Top)
              .transition(TransitionEffect.OPACITY)
            }
          }
          .width('100%')
          .padding(Tokens.cardPadding)
          .backgroundColor($r('app.color.card_bg'))
          .borderRadius(Tokens.radiusCard)
          .border({ width: 1, color: $r('app.color.divider') })
          .alignItems(HorizontalAlign.Start)

          // ── 运行日志 ────────────────────────────────────
          Column({ space: Tokens.space2 }) {
            Row() {
              Text('运行日志')
                .fontSize(Tokens.fontLabel)
                .fontColor($r('app.color.text_muted'))
              Blank()
              if (this.showLogs) {
                Text('清空')
                  .fontSize(Tokens.fontLabel)
                  .fontColor($r('app.color.accent'))
                  .margin({ right: Tokens.space3 })
                  .onClick(() => {
                    Logger.clear();
                    this.logs = [];
                  })
              }
              Text(this.showLogs ? '收起' : '展开')
                .fontSize(Tokens.fontLabel)
                .fontColor($r('app.color.accent'))
                .onClick(() => {
                  this.getUIContext().animateTo({ duration: 220, curve: Curve.EaseInOut }, () => {
                    this.showLogs = !this.showLogs;
                  });
                  this.refresh();
                })
            }
            .width('100%')

            if (this.showLogs) {
              ForEach(this.logs, (line: string) => {
                Text(line)
                  .fontSize(Tokens.fontCaption)
                  .lineHeight(15)
                  .fontColor($r('app.color.log_text'))
                  .width('100%')
              }, (line: string, index: number) => `${index}-${line}`)
            }
          }
          .width('100%')
          .padding(Tokens.cardPadding)
          .backgroundColor($r('app.color.log_bg'))
          .borderRadius(Tokens.radiusCard)
          .alignItems(HorizontalAlign.Start)
        }
        .width('100%')
        .padding({ left: Tokens.pagePadding, right: Tokens.pagePadding, top: Tokens.cardGap, bottom: Tokens.space8 })
        .constraintSize({ minHeight: '100%' })
        .justifyContent(FlexAlign.Start)
      }
      .layoutWeight(1)
      .scrollBar(BarState.Off)
    }
    .width('100%')
    .height('100%')
    .backgroundColor($r('app.color.page_bg'))
  }
}
