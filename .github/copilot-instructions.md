# MicCast — Copilot 项目指令

完整约定见仓库根目录的 [AGENTS.md](../AGENTS.md)，这里只做指引。

**开工前必须读的几条（详见 AGENTS.md）：**

1. HarmonyOS 工程在 `harmony/`，Windows 工程在 `windows/`，两者独立，不要把 HM 文件放回根目录
2. 构建签名要先用 `build-profile.local.json5` 临时替换 `build-profile.json5`，否则 HAP 装不上
3. 设备自动化**禁止硬编码坐标** —— 必须 dump → 按 text 定位 → 点击 → 立即验证
4. dump 出来的中文是乱码，**不能靠猜**，用截图确认
5. `SOURCE_TYPE_VOICE_COMMUNICATION` 在非通话场景返回全零，禁止使用
6. 测音频链路不能用被测设备自放或 PC 的 TTS（都会触发 AEC 消除）
