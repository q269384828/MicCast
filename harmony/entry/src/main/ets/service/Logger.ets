import { hilog } from '@kit.PerformanceAnalysisKit';

const DOMAIN: number = 0x0A11;
const TAG: string = 'MicCast';

/** 轻量日志：既进 hilog，也保留最近若干行供 UI 显示 */
export class Logger {
  static lines: string[] = [];

  static log(tag: string, message: string): void {
    const now: Date = new Date();
    const hh: string = now.getHours().toString().padStart(2, '0');
    const mm: string = now.getMinutes().toString().padStart(2, '0');
    const ss: string = now.getSeconds().toString().padStart(2, '0');
    const line: string = `${hh}:${mm}:${ss} [${tag}] ${message}`;
    Logger.lines.push(line);
    while (Logger.lines.length > 200) {
      Logger.lines.shift();
    }
    hilog.info(DOMAIN, TAG, '%{public}s', line);
  }

  static clear(): void {
    Logger.lines = [];
  }
}
