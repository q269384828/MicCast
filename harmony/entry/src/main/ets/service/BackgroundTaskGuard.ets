import { backgroundTaskManager } from '@kit.BackgroundTasksKit';
import { common, wantAgent, WantAgent } from '@kit.AbilityKit';
import { Logger } from './Logger';

/**
 * 后台长时任务（audioRecording）。
 * 注意：鸿蒙要求录音必须在前台启动，启动后立即申请长时任务，
 * 系统会做运行态一致性校验（声明了录音任务但没在录音会被取消）。
 */
export class BackgroundTaskGuard {
  private running: boolean = false;

  isRunning(): boolean {
    return this.running;
  }

  async start(context: common.UIAbilityContext): Promise<boolean> {
    if (this.running) {
      return true;
    }
    try {
      const wantAgentInfo: wantAgent.WantAgentInfo = {
        wants: [
          {
            bundleName: context.abilityInfo.bundleName,
            abilityName: context.abilityInfo.name
          }
        ],
        actionType: wantAgent.OperationType.START_ABILITY,
        requestCode: 100,
        actionFlags: [wantAgent.WantAgentFlags.UPDATE_PRESENT_FLAG]
      };
      const agent: WantAgent = await wantAgent.getWantAgent(wantAgentInfo);
      await backgroundTaskManager.startBackgroundRunning(
        context,
        backgroundTaskManager.BackgroundMode.AUDIO_RECORDING,
        agent
      );
      this.running = true;
      Logger.log('bg', 'background long task started (audioRecording)');
      return true;
    } catch (err) {
      Logger.log('bg', `startBackgroundRunning failed: ${err}`);
      return false;
    }
  }

  async stop(context: common.UIAbilityContext): Promise<void> {
    if (!this.running) {
      return;
    }
    try {
      await backgroundTaskManager.stopBackgroundRunning(context);
    } catch (err) {
      Logger.log('bg', `stopBackgroundRunning failed: ${err}`);
    }
    this.running = false;
    Logger.log('bg', 'background long task stopped');
  }
}
