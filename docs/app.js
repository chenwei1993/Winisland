(() => {
    const repo = "chenwei1993/Winisland";
    const fallbackVersion = "1.2.1";

    // ── Latest release ────────────────────────────────────────────────
    const versionHero = document.getElementById("version-hero");
    const versionMain = document.getElementById("version-main");
    const downloadHero = document.getElementById("download-hero");
    const downloadMain = document.getElementById("download-main");
    const downloadSize = document.getElementById("download-size");

    async function loadLatestRelease() {
        try {
            const response = await fetch(`https://api.github.com/repos/${repo}/releases/latest`, {
                headers: { Accept: "application/vnd.github+json" }
            });
            if (!response.ok) return;
            const release = await response.json();
            const tag = (release.tag_name || "").replace(/^v/, "");
            if (tag) {
                if (versionHero) versionHero.textContent = tag;
                if (versionMain) versionMain.textContent = tag;
            }

            const exe = (release.assets || []).find((asset) =>
                /^WinIsland\.exe$/i.test(asset.name || ""));
            const url = exe?.browser_download_url || release.html_url;
            if (url) {
                if (downloadHero) downloadHero.href = url;
                if (downloadMain) downloadMain.href = url;
            }

            if (exe?.size && downloadSize) {
                downloadSize.textContent = `约 ${(exe.size / 1024 / 1024).toFixed(1)} MB`;
            }
        } catch {
            // Keep the static fallback pointing at the releases page.
        }
    }

    if (versionHero && !versionHero.textContent.trim()) versionHero.textContent = fallbackVersion;
    if (versionMain && !versionMain.textContent.trim()) versionMain.textContent = fallbackVersion;
    loadLatestRelease();

    // ── Screenshot tabs ───────────────────────────────────────────────
    const shots = {
        usage: {
            src: "assets/shot-usage.png",
            alt: "WinIsland 的 AI 用量面板",
            caption: "按平台顺序展示额度，接近上限时自动提醒。"
        },
        system: {
            src: "assets/shot-system.png",
            alt: "WinIsland 的系统状态、音量和耳机控制面板",
            caption: "CPU、内存、网络、音量、亮度与耳机控制，一页看完。"
        },
        calendar: {
            src: "assets/shot-calendar.png",
            alt: "WinIsland 的日历面板",
            caption: "月视图、今日高亮，以及实时时钟与周数。"
        },
        settings: {
            src: "assets/shot-settings.png",
            alt: "WinIsland 的设置面板",
            caption: "字体大小、窗口避让、开机自启与剪贴板开关。"
        },
        ai: {
            src: "assets/shot-ai.png",
            alt: "WinIsland 的 AI 用量配置窗口",
            caption: "每个平台单独配置 Key、显示顺序与刷新间隔，Key 本地加密。"
        }
    };

    const screenImage = document.getElementById("screen-image");
    const screenCaption = document.getElementById("screen-caption");
    const tabs = Array.from(document.querySelectorAll(".tab"));

    function selectShot(key) {
        const shot = shots[key];
        if (!shot || !screenImage) return;
        screenImage.src = shot.src;
        screenImage.alt = shot.alt;
        screenImage.classList.toggle("portrait", key === "ai");
        if (screenCaption) screenCaption.textContent = shot.caption;
        tabs.forEach((tab) => {
            const active = tab.dataset.shot === key;
            tab.classList.toggle("active", active);
            tab.setAttribute("aria-selected", active ? "true" : "false");
        });
    }

    tabs.forEach((tab) => {
        tab.addEventListener("click", () => selectShot(tab.dataset.shot));
    });

    // ── Lightbox ──────────────────────────────────────────────────────
    const lightbox = document.getElementById("lightbox");
    const lightboxImage = document.getElementById("lightbox-image");
    const lightboxClose = document.querySelector(".lightbox-close");

    function openLightbox(src, alt) {
        if (!lightbox || !lightboxImage) return;
        lightboxImage.src = src;
        lightboxImage.alt = alt || "";
        lightbox.hidden = false;
        document.body.style.overflow = "hidden";
    }

    function closeLightbox() {
        if (!lightbox || !lightboxImage) return;
        lightbox.hidden = true;
        lightboxImage.src = "";
        document.body.style.overflow = "";
    }

    document.querySelectorAll(".screen-stage .shot, .hero-frame .shot").forEach((image) => {
        image.style.cursor = "zoom-in";
        image.addEventListener("click", () => openLightbox(image.src, image.alt));
    });

    lightboxClose?.addEventListener("click", closeLightbox);
    lightbox?.addEventListener("click", (event) => {
        if (event.target === lightbox) closeLightbox();
    });

    // ── Mobile navigation ─────────────────────────────────────────────
    const nav = document.querySelector(".nav");
    const menuButton = document.querySelector(".menu-button");

    menuButton?.addEventListener("click", () => {
        const open = nav.classList.toggle("menu-open");
        menuButton.setAttribute("aria-expanded", open ? "true" : "false");
    });

    document.querySelectorAll(".nav-links a").forEach((link) => {
        link.addEventListener("click", () => {
            nav?.classList.remove("menu-open");
            menuButton?.setAttribute("aria-expanded", "false");
        });
    });

    document.addEventListener("keydown", (event) => {
        if (event.key === "Escape") {
            closeLightbox();
            nav?.classList.remove("menu-open");
            menuButton?.setAttribute("aria-expanded", "false");
        }
    });

    // ── Misc ──────────────────────────────────────────────────────────
    const year = document.getElementById("year");
    if (year) year.textContent = String(new Date().getFullYear());
})();
