// Hiển thị ảnh đã chụp: nhận bytes JPEG từ .NET dưới dạng stream nhị phân (DotNetStreamReference),
// tạo object URL cho <img>. Không dùng chuỗi Base64, không lưu ảnh vào wwwroot.
(() => {
    'use strict';

    const urls = new WeakMap();

    function release(img) {
        const url = urls.get(img);
        if (url) {
            URL.revokeObjectURL(url);
            urls.delete(img);
        }
    }

    async function show(img, streamRef, contentType) {
        const buffer = await streamRef.arrayBuffer();
        const url = URL.createObjectURL(new Blob([buffer], { type: contentType || 'image/jpeg' }));
        release(img);
        urls.set(img, url);
        img.src = url;
    }

    function clear(img) {
        if (!img) {
            return;
        }
        release(img);
        img.removeAttribute('src');
    }

    window.photoboothPhotoView = Object.freeze({ show, clear });
})();
