-- 将本脚本放入 MPV 的 scripts 目录（如 %APPDATA%\mpv\scripts\ 或便携版 portable_config\scripts\）
-- 配合本地运行的 PixelBarLyrics，可将播放中的软字幕实时推送到花再 Halo PixelBar 音响屏幕

local utils = require("mp.utils")

local API_URL = "http://127.0.0.1:18421/text"
local CLEAR_URL = "http://127.0.0.1:18421/clear"
local TTL_MS = 6000

local last_text = ""

local function clean_subtitle(raw)
    if not raw or raw == "" then
        return ""
    end
    -- 清理 ASS/SSA 样式标签（例如 {\an8}）并合并换行
    local s = raw:gsub("{[^}]*}", "")
    s = s:gsub("[\r\n]+", " ")
    s = s:gsub("%s+", " ")
    s = s:match("^%s*(.-)%s*$") or ""
    return s
end

local function post_json(url, tbl)
    local body, err = utils.format_json(tbl)
    if not body then
        return
    end
    mp.command_native_async({
        name = "subprocess",
        playback_only = false,
        detach = false,
        args = {
            "curl.exe",
            "-s",
            "-m", "1",
            "-X", "POST",
            url,
            "-H", "Content-Type: application/json; charset=utf-8",
            "-d", body
        }
    }, function() end)
end

mp.observe_property("sub-text", "string", function(_, val)
    local clean = clean_subtitle(val)
    if clean == "" or clean == last_text then
        return
    end
    last_text = clean
    post_json(API_URL, {
        text = clean,
        source = "mpv",
        ttlMs = TTL_MS
    })
end)

mp.register_event("shutdown", function()
    post_json(CLEAR_URL, {})
end)
