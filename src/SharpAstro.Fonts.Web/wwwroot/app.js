// Interop for the side-by-side. The point of registerFont is that the browser half and our half
// are handed the SAME ArrayBuffer the app fetched — neither side can be showing a different file.
window.fontsCompare = {
    registerFont: async function (family, bytes) {
        const face = new FontFace(family, bytes.buffer ?? bytes);
        await face.load();
        document.fonts.add(face);
        return true;
    },

    draw: function (canvas, width, height, bytes) {
        canvas.width = width;
        canvas.height = height;
        const ctx = canvas.getContext('2d');
        ctx.putImageData(new ImageData(new Uint8ClampedArray(bytes), width, height), 0, 0);
    },

    // The browser's own advance for the same string — the one number in this page that is a
    // genuine oracle rather than a visual impression.
    //
    // Measures the element that is ACTUALLY ON SCREEN, via a Range over its text. The obvious
    // implementation, ctx.measureText on a detached canvas, silently measured something else: a
    // canvas context inherits none of the pane's CSS, so it kept applying automatic optical sizing
    // and reported Roboto Flex 25% narrower than the pane beside it was drawing. Measuring the live
    // node makes it structurally impossible for the number and the picture to disagree.
    measureElement: function (el) {
        const range = document.createRange();
        range.selectNodeContents(el);
        return range.getBoundingClientRect().width;
    },

    engine: function () {
        return navigator.userAgent;
    },

    // Stopwatch under mono-wasm reports 0.000 ms for work that plainly takes longer, so timings
    // come from the browser clock instead. Note performance.now() is deliberately coarsened
    // (~100 us) unless the page is cross-origin isolated — treat sub-0.1 ms readings as noise.
    now: function () {
        return performance.now();
    }
};
