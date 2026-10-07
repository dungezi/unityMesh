# 维护说明

主要源码位于 `Packages/com.codex.stylized-mesh`。`ValidationProject` 是独立的 Unity 2022.3.22f1 验证项目；插件副本由验证脚本自动复制，不作为第二份源码维护。

修改网格、表情、蒙皮或撤销行为后，执行 `tools/Compile.ps1` 和 `tools/RunUnityValidation.ps1`。修改减面算法时另执行 `tools/CheckAlgorithms.ps1`。脚本的 Unity 和 Mono 路径可通过参数指定。

发布新版本时：

1. 更新 `Packages/com.codex.stylized-mesh/package.json` 的版本，以及 README 中的安装包名称。
2. 更新 `CHANGELOG.md`，在 `docs/releases` 中编写对应版本的发布说明。
3. 执行验证。运行 `python tools/BuildDistribution.py` 生成安装包与 UPM ZIP。
4. 在验证项目中运行 `ValidationRunner.ImportAndExport`，检查安装包导入并由 Unity 原生重新导出。
5. 运行 `python tools/BuildDistribution.py --use-unity-export` 核验最终安装包。
6. 暂存源码，运行 `python tools/AuditPublication.py --archives` 检查公开内容；扫描仅报告匹配位置，不输出匹配值。
7. 提交并推送源码，创建与版本一致的 `v版本号` 标签。创建 GitHub Release 并附上两个安装包与验证记录。

可使用 `python tools/PublishGitHub.py inspect --owner 你的GitHub用户名` 检查 Git 凭据助手的登录状态。在所有内容审查并提交后，运行 `python tools/PublishGitHub.py publish --owner 你的GitHub用户名 --repository unityMesh --public` 上传源码并发布版本。脚本读取当前包版本，先将所有附件上传到草稿，再发布 Release；不会覆盖已有版本标签。省略 `--public` 时，新建仓库默认为私有。凭据只在内存中使用。

报告问题时，请提供 Unity 版本、使用的功能、复现步骤，以及相关报错。真实模型文件请遵守模型作者的分发许可。

自动检查使用合成网格验证数据和编辑行为，真实模型、鼠标交互与 VRChat 上传仍需实际检查。不要将尚未执行的检查写为通过。

第三方减面算法的固定提交、修改记录和 MIT 许可证位于 `Editor/ThirdParty`，修改或分发时保留相关声明。
