// Please see documentation at https://learn.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

// Write your JavaScript code.
window.downloadFile = (filename, contentType, content) => {
    // Blazor may marshal byte[] as either a Uint8Array or a base64 string depending
    // on the hosting/runtime path. Decode the latter so exports are not corrupted.
    const fileContent = typeof content === "string"
        ? Uint8Array.from(atob(content), character => character.charCodeAt(0))
        : content;
    const file = new File([fileContent], filename, { type: contentType });
    const exportUrl = URL.createObjectURL(file);
    const a = document.createElement("a");
    a.href = exportUrl;
    a.download = filename;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    URL.revokeObjectURL(exportUrl);
}

window.merconiqTheme = {
    getDarkMode: () => {
        const stored = window.localStorage.getItem("merconiq-dark-mode");
        return stored === null
            ? window.matchMedia("(prefers-color-scheme: dark)").matches
            : stored === "true";
    },
    setDarkMode: enabled => {
        window.localStorage.setItem("merconiq-dark-mode", enabled ? "true" : "false");
    }
};
