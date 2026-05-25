function getBrowserName(userAgent) {
    if (!userAgent) {
        return "";
    }

    const checks = [
        ["Edg/", "Edge"],
        ["OPR/", "Opera"],
        ["Chrome/", "Chrome"],
        ["Firefox/", "Firefox"],
        ["Safari/", "Safari"]
    ];

    for (const [token, name] of checks) {
        const index = userAgent.indexOf(token);
        if (index >= 0) {
            const version = userAgent.slice(index + token.length).split(/[ )]/)[0];
            return { name, version };
        }
    }

    return { name: "Browser", version: "" };
}

export function getClientEnvironment() {
    const nav = window.navigator ?? {};
    const userAgent = nav.userAgent ?? "";
    const platform = nav.userAgentData?.platform ?? nav.platform ?? "";
    const browser = getBrowserName(userAgent);

    let gpuVendor = "";
    let gpuRenderer = "";
    let webGlSupported = false;

    try {
        const canvas = document.createElement("canvas");
        const gl = canvas.getContext("webgl") || canvas.getContext("experimental-webgl");

        if (gl) {
            webGlSupported = true;

            const debugInfo = gl.getExtension("WEBGL_debug_renderer_info");
            if (debugInfo) {
                gpuVendor = gl.getParameter(debugInfo.UNMASKED_VENDOR_WEBGL) || "";
                gpuRenderer = gl.getParameter(debugInfo.UNMASKED_RENDERER_WEBGL) || "";
            } else {
                gpuVendor = gl.getParameter(gl.VENDOR) || "";
                gpuRenderer = gl.getParameter(gl.RENDERER) || "";
            }
        }
    } catch {
        webGlSupported = false;
    }

    return {
        platform,
        userAgent,
        browserName: browser.name,
        browserVersion: browser.version,
        gpuVendor,
        gpuRenderer,
        webGlSupported,
        maxTouchPoints: nav.maxTouchPoints ?? 0,
        viewportWidth: window.innerWidth || 0,
        viewportHeight: window.innerHeight || 0
    };
}
