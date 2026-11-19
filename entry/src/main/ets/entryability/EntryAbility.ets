import { AbilityConstant, ConfigurationConstant, UIAbility, Want } from '@kit.AbilityKit';
import { window } from '@kit.ArkUI';
import { Logger } from '../service/Logger';

export default class EntryAbility extends UIAbility {
  onCreate(want: Want, launchParam: AbilityConstant.LaunchParam): void {
    // 跟随系统深浅色，由 resources/base 与 resources/dark 两套 color.json 提供配色
    this.context.getApplicationContext().setColorMode(ConfigurationConstant.ColorMode.COLOR_MODE_NOT_SET);
    Logger.log('ability', 'onCreate');
  }

  onWindowStageCreate(windowStage: window.WindowStage): void {
    windowStage.loadContent('pages/Index', (err) => {
      if (err.code) {
        Logger.log('ability', `loadContent failed: ${JSON.stringify(err)}`);
        return;
      }
      Logger.log('ability', 'content loaded');
    });
  }

  onDestroy(): void {
    Logger.log('ability', 'onDestroy');
  }

  onForeground(): void {
    Logger.log('ability', 'onForeground');
  }

  onBackground(): void {
    Logger.log('ability', 'onBackground');
  }
}
