// ForgeLink theme handling. Loaded in <head> (not deferred) so a saved theme applies before first paint.
// Default follows the OS; the toggle stores an explicit choice per device.
(function () {
    var root = document.documentElement;
    try {
        var saved = localStorage.getItem("fl-theme");
        if (saved === "light" || saved === "dark") root.dataset.theme = saved;
    } catch (e) { /* storage blocked: follow the OS */ }

    window.flToggleTheme = function () {
        var current = root.dataset.theme ||
            (window.matchMedia && matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light");
        var next = current === "dark" ? "light" : "dark";
        root.dataset.theme = next;
        try { localStorage.setItem("fl-theme", next); } catch (e) { }
    };
})();
