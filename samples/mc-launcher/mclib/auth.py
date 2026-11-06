# -*- coding: utf-8 -*-
"""登录：离线一条路，微软一条路。

微软走设备码（和 PCV 里那个 C# 模块同一套流程），这样不用内置密码，
也不需要在本地开浏览器回调端口 —— 在浏览器里输个码就行。
"""
import hashlib
import json
import os
import time
import urllib.parse
import urllib.request
import uuid as uuidlib

import net

DEVICE_CODE = "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode"
TOKEN = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token"
XBL = "https://user.auth.xboxlive.com/user/authenticate"
XSTS = "https://xsts.auth.xboxlive.com/xsts/authorize"
MC_LOGIN = "https://api.minecraftservices.com/authentication/login_with_xbox"
MC_PROFILE = "https://api.minecraftservices.com/minecraft/profile"
SCOPE = "XboxLive.signin offline_access"


def offline(name):
    """离线登录：UUID 按 OfflinePlayer:<名字> 的 MD5 生成，各启动器都认这个算法。"""
    digest = bytearray(hashlib.md5(("OfflinePlayer:" + name).encode("utf-8")).digest())
    digest[6] = (digest[6] & 0x0F) | 0x30   # 版本位：名字型 UUID
    digest[8] = (digest[8] & 0x3F) | 0x80   # 变体位
    return {
        "name": name,
        "uuid": str(uuidlib.UUID(bytes=bytes(digest))),
        "access_token": "0" * 32,
        "user_type": "legacy",
        "xuid": "",
        "kind": "offline",
    }


def _post_form(url, data):
    body = urllib.parse.urlencode(data).encode("utf-8")
    request = urllib.request.Request(url, data=body, headers={
        "Content-Type": "application/x-www-form-urlencoded",
        "User-Agent": net.UA,
    })
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            return response.status, response.read().decode("utf-8")
    except urllib.error.HTTPError as error:
        return error.code, error.read().decode("utf-8", "replace")


def _post_json(url, payload, bearer=None):
    body = json.dumps(payload).encode("utf-8")
    headers = {"Content-Type": "application/json", "Accept": "application/json", "User-Agent": net.UA}
    if bearer:
        headers["Authorization"] = "Bearer " + bearer
    request = urllib.request.Request(url, data=body, headers=headers)
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            return response.status, response.read().decode("utf-8")
    except urllib.error.HTTPError as error:
        return error.code, error.read().decode("utf-8", "replace")


def microsoft(client_id, on_progress):
    if not client_id:
        raise RuntimeError(
            "没有 Microsoft ClientId。\n"
            "  用 `login ms <你的ClientId>` 临时指定，或在 PCV 的配置里设 MsClientId。\n"
            "  ClientId 要去 Azure 门户自己申请（开源版不带内置的，带了也不敢用）。")

    status, text = _post_form(DEVICE_CODE, {"client_id": client_id, "scope": SCOPE})
    if status != 200:
        raise RuntimeError("申请设备码失败：%s %s" % (status, text[:200]))

    device = json.loads(text)
    on_progress("请在浏览器打开 %s ，输入代码：%s" % (device["verification_uri"], device["user_code"]))
    on_progress("（%d 秒内有效，授权完这里会自动继续）" % device["expires_in"])

    interval = max(1, int(device.get("interval", 5)))
    deadline = time.time() + device["expires_in"]
    tokens = None

    while time.time() < deadline:
        time.sleep(interval)
        status, text = _post_form(TOKEN, {
            "grant_type": "urn:ietf:params:oauth:grant-type:device_code",
            "device_code": device["device_code"],
            "client_id": client_id,
        })
        if status == 200:
            tokens = json.loads(text)
            break

        try:
            error = json.loads(text).get("error", "")
        except ValueError:
            error = text[:120]

        if error == "authorization_pending":
            on_progress("还在等你授权…")
        elif error == "slow_down":
            interval += 5
            on_progress("授权服务要求放慢轮询，间隔调到 %d 秒" % interval)
        elif error == "expired_token":
            raise RuntimeError("设备码过期了，重新来一次")
        elif error == "authorization_declined":
            raise RuntimeError("你拒绝了授权")
        else:
            raise RuntimeError("登录失败：%s" % error)

    if not tokens:
        raise RuntimeError("等超时了，没有拿到令牌")

    oauth_access = tokens["access_token"]
    oauth_refresh = tokens.get("refresh_token", "")

    on_progress("OAuth 过了，正在换 Xbox Live 令牌…")
    status, text = _post_json(XBL, {
        "Properties": {"AuthMethod": "RPS", "SiteName": "user.auth.xboxlive.com",
                       "RpsTicket": "d=" + oauth_access},
        "RelyingParty": "http://auth.xboxlive.com",
        "TokenType": "JWT",
    })
    if status != 200:
        raise RuntimeError("Xbox Live 认证失败：%s %s" % (status, text[:200]))
    xbl_token = json.loads(text)["Token"]

    on_progress("正在换 XSTS 授权…")
    status, text = _post_json(XSTS, {
        "Properties": {"SandboxId": "RETAIL", "UserTokens": [xbl_token]},
        "RelyingParty": "rp://api.minecraftservices.com/",
        "TokenType": "JWT",
    })
    if status != 200:
        raise RuntimeError(_xsts_hint(text))

    xsts = json.loads(text)
    user_hash = xsts["DisplayClaims"]["xui"][0]["uhs"]

    on_progress("正在换 Minecraft 令牌…")
    status, text = _post_json(MC_LOGIN, {"identityToken": "XBL3.0 x=%s;%s" % (user_hash, xsts["Token"])})
    if status != 200:
        raise RuntimeError("Minecraft 登录失败：%s %s" % (status, text[:200]))
    mc_token = json.loads(text)["access_token"]

    request = urllib.request.Request(MC_PROFILE, headers={
        "Authorization": "Bearer " + mc_token, "User-Agent": net.UA})
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            profile = json.loads(response.read().decode("utf-8"))
    except urllib.error.HTTPError as error:
        if error.code == 404:
            raise RuntimeError("这个账号没有 Minecraft: Java 版（没买，或者档案还没建）")
        raise

    return {
        "name": profile["name"],
        "uuid": profile["id"],
        "access_token": mc_token,
        "user_type": "msa",
        "xuid": user_hash,
        "refresh_token": oauth_refresh,
        "kind": "microsoft",
    }


def _xsts_hint(text):
    try:
        xerr = json.loads(text).get("XErr")
    except ValueError:
        return "XSTS 授权失败：" + text[:200]

    hints = {
        2148916233: "这个微软账号还没建 Xbox 档案，先去 xbox.com 登录一次",
        2148916235: "所在地区不支持 Xbox Live",
        2148916236: "需要先完成成年人验证",
        2148916237: "需要先完成成年人验证",
        2148916238: "未成年人账号，得先加入家庭组",
    }
    return "XSTS 授权失败（XErr=%s）：%s" % (xerr, hints.get(xerr, "原因不明"))
