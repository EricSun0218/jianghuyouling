# 第三方依赖与许可

项目自有内容使用 [MIT](LICENSE)。以下依赖的许可不被项目许可证替代。

## Newtonsoft.Json 13.0.3

来源：https://github.com/JamesNK/Newtonsoft.Json

Core 通过 NuGet 引用；游戏内包使用游戏提供的程序集。本项目不打包游戏目录中的 DLL。
Newtonsoft.Json 的 MIT 许可全文如下：

> The MIT License (MIT)
>
> Copyright (c) 2007 James Newton-King
>
> Permission is hereby granted, free of charge, to any person obtaining a copy of
> this software and associated documentation files (the "Software"), to deal in
> the Software without restriction, including without limitation the rights to
> use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of
> the Software, and to permit persons to whom the Software is furnished to do so,
> subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in all
> copies or substantial portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
> IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS
> FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
> COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER
> IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
> CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

## Microsoft.NETFramework.ReferenceAssemblies 1.0.3

.NET Framework 构建引用程序集，仅用于构建，不随 Mod 分发。
来源：https://github.com/microsoft/dotnet
许可：https://github.com/microsoft/dotnet/blob/main/LICENSE

## 游戏安装提供的依赖

Frontend / Backend 引用本机《太吾绘卷》安装中的游戏、Unity、Harmony、
Steamworks.NET、Redzen 等程序集，项目文件将这些引用设为 `Private=false`。
这些文件不是仓库内容，也不在 Mod 发布包白名单内；构建者需自行合法安装游戏。
相关程序集及游戏资产适用各自权利人的许可，不受本仓库 MIT 许可覆盖。
