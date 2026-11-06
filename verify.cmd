@echo off
chcp 65001 >nul
setlocal
pushd "%~dp0"

echo [1/4] 构建
dotnet build PCL.Modular.sln -v:q
if errorlevel 1 goto fail

echo.
echo [2/4] 加载器故障注入（验证它真的会判红）
"src\PCL.Modular.Cli\bin\Debug\PCL.Modular.Cli.exe" --inject-fault
if errorlevel 1 goto fail

echo.
echo [3/4] 安装器故障注入（坏包 / zip slip / 覆盖 / 回滚）
"src\PCL.Modular.Cli\bin\Debug\PCL.Modular.Cli.exe" --package-selftest
if errorlevel 1 goto fail

echo.
echo [4/4] 真实模块加载
"src\PCL.Modular.Cli\bin\Debug\PCL.Modular.Cli.exe" --modules "%~dp0src\PCL.Shell\bin\Debug\modules"
if errorlevel 1 goto fail

echo.
echo 全部校验通过。
popd
exit /b 0

:fail
echo.
echo 校验失败，见上面的输出。
popd
exit /b 1
