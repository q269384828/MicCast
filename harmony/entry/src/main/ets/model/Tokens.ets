/**
 * 设计 token，值与 docs/design-system.md 一一对应。
 * Windows 端对应文件：windows/MicCastReceiver/Ui/Theme.cs
 *
 * 颜色走 resources 下的 color.json（base 与 dark 两套，跟随系统深浅色），
 * 这里只放尺寸、字号、动效这类不随主题变化的数值。
 */
export class Tokens {
  // 圆角
  static readonly radiusHero: number = 22;
  static readonly radiusCard: number = 16;
  static readonly radiusControl: number = 10;
  static readonly radiusSmall: number = 6;
  /** 胶囊：传一个大于高度一半的值，系统会自己夹到满圆 */
  static readonly radiusPill: number = 999;

  // 间距（4 的倍数）
  static readonly space1: number = 4;
  static readonly space2: number = 8;
  static readonly space3: number = 12;
  static readonly space4: number = 16;
  static readonly space5: number = 20;
  static readonly space6: number = 24;
  static readonly space8: number = 32;
  static readonly pagePadding: number = 20;
  static readonly cardPadding: number = 16;
  static readonly cardGap: number = 12;

  // 字号（fp，约等于 px）
  static readonly fontHero: number = 22;
  static readonly fontMetric: number = 20;
  static readonly fontTitle: number = 13;
  static readonly fontButton: number = 15;
  static readonly fontBody: number = 13;
  static readonly fontLabel: number = 12;
  static readonly fontCaption: number = 11;

  // 动效
  static readonly durFast: number = 120;
  static readonly durBase: number = 200;

  // 控件尺寸
  static readonly buttonHeight: number = 44;
  static readonly segmentedHeight: number = 34;
  static readonly levelBarHeight: number = 6;
  static readonly iconSize: number = 34;
}