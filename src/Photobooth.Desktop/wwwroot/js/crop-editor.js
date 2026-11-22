// Trình chỉnh vùng cắt (Manual Crop) — chỉ là XEM TRƯỚC TẠM trong lúc kéo/zoom.
// Ảnh thành phẩm luôn được render lại bằng Photo Composer (C#) sau khi khách bấm "Áp dụng".
//
// Trạng thái là giá trị CHUẨN HÓA theo ảnh đang hiển thị (đã lật gương nếu có):
//   zoom  : 1 = vùng cắt cơ sở của Auto Crop (ảnh vừa phủ kín ô)
//   fx, fy: tâm vùng cắt, 0..1 theo chiều rộng/cao ảnh
// Công thức giống hệt CropCalculator.Cover (C#), nên không phụ thuộc kích thước cửa sổ hay DPI:
// mọi phép tính dùng CSS pixel của chính khung xem lúc đó, không dùng tọa độ màn hình tuyệt đối.
(() => {
    'use strict';

    const editors = new Map();
    let nextId = 1;
    const NOTIFY_DELAY_MS = 120;
    const WHEEL_ZOOM_SPEED = 0.0015;

    const clamp = (value, min, max) => Math.min(Math.max(value, min), max);

    // Vùng cắt (pixel ảnh) — giống CropCalculator.Cover.
    function cropRect(s) {
        const sourceRatio = s.imageWidth / s.imageHeight;
        const targetRatio = s.targetWidth / s.targetHeight;
        let width;
        let height;
        if (sourceRatio > targetRatio) {
            height = s.imageHeight;
            width = s.imageHeight * targetRatio;
        } else {
            width = s.imageWidth;
            height = s.imageWidth / targetRatio;
        }
        width /= s.zoom;
        height /= s.zoom;
        const x = clamp(s.imageWidth * clamp(s.fx, 0, 1) - width / 2, 0, s.imageWidth - width);
        const y = clamp(s.imageHeight * clamp(s.fy, 0, 1) - height / 2, 0, s.imageHeight - height);
        return { x, y, width, height };
    }

    // Đưa tâm về vị trí thực sự đạt được (sau khi kẹp) để trạng thái luôn chuẩn tắc.
    function canonicalize(s) {
        s.zoom = clamp(s.zoom, s.minZoom, s.maxZoom);
        const r = cropRect(s);
        s.fx = (r.x + r.width / 2) / s.imageWidth;
        s.fy = (r.y + r.height / 2) / s.imageHeight;
        return r;
    }

    function render(e) {
        const s = e.state;
        const r = canonicalize(s);
        const viewportWidth = e.viewport.clientWidth;
        if (viewportWidth <= 0) {
            return;
        }
        const scale = viewportWidth / r.width;
        e.image.style.width = `${s.imageWidth * scale}px`;
        e.image.style.height = `${s.imageHeight * scale}px`;
        e.image.style.transform = `translate(${-r.x * scale}px, ${-r.y * scale}px)`;
    }

    function scheduleRender(e) {
        if (e.frame) {
            return;
        }
        e.frame = requestAnimationFrame(() => {
            e.frame = 0;
            render(e);
        });
    }

    function notify(e, immediate) {
        clearTimeout(e.notifyTimer);
        const send = () => {
            e.notifyTimer = 0;
            const s = e.state;
            e.dotnet.invokeMethodAsync('OnCropChanged', s.zoom, s.fx, s.fy).catch(() => { /* component đã đóng */ });
        };
        if (immediate) {
            send();
        } else {
            e.notifyTimer = setTimeout(send, NOTIFY_DELAY_MS);
        }
    }

    // Cử chỉ: 1 ngón/chuột = kéo ảnh; 2 ngón = chụm/mở để phóng to (đồng thời kéo theo trung điểm).
    // Mỗi khi số ngón thay đổi, cử chỉ được bắt đầu lại từ trạng thái hiện tại nên không bị "nhảy" ảnh.
    function beginGesture(e) {
        const points = [...e.pointers.values()];
        if (points.length === 0) {
            e.gesture = null;
            return;
        }
        const r = canonicalize(e.state);
        const box = e.viewport.getBoundingClientRect();
        // Pixel ảnh trên mỗi CSS pixel của khung xem (cùng giá trị cho hai chiều vì khung đúng tỉ lệ ô).
        const imagePerCss = r.width / Math.max(1, box.width);
        const mid = midpoint(points);
        e.gesture = {
            startZoom: e.state.zoom,
            startDistance: points.length > 1 ? distance(points[0], points[1]) : 0,
            // Điểm ảnh đang nằm dưới trung điểm các ngón tay: giữ nó đứng yên dưới ngón tay khi kéo/phóng to.
            anchorX: r.x + (mid.x - box.left) * imagePerCss,
            anchorY: r.y + (mid.y - box.top) * imagePerCss,
        };
    }

    function updateGesture(e) {
        const g = e.gesture;
        const points = [...e.pointers.values()];
        if (!g || points.length === 0) {
            return;
        }
        const s = e.state;
        if (points.length > 1 && g.startDistance > 0) {
            s.zoom = clamp(g.startZoom * distance(points[0], points[1]) / g.startDistance, s.minZoom, s.maxZoom);
        }
        const box = e.viewport.getBoundingClientRect();
        const size = cropRect({ ...s, fx: 0.5, fy: 0.5 });
        const imagePerCss = size.width / Math.max(1, box.width);
        const mid = midpoint(points);
        const x = g.anchorX - (mid.x - box.left) * imagePerCss;
        const y = g.anchorY - (mid.y - box.top) * imagePerCss;
        s.fx = (x + size.width / 2) / s.imageWidth;
        s.fy = (y + size.height / 2) / s.imageHeight;
        scheduleRender(e);
        if (points.length > 1) {
            // Cập nhật số % trên thanh trượt trong lúc chụm (gộp, không gửi theo từng sự kiện).
            notify(e, false);
        }
    }

    function midpoint(points) {
        const n = Math.min(points.length, 2);
        let x = 0;
        let y = 0;
        for (let i = 0; i < n; i++) {
            x += points[i].x;
            y += points[i].y;
        }
        return { x: x / n, y: y / n };
    }

    function distance(a, b) {
        return Math.hypot(a.x - b.x, a.y - b.y);
    }

    function onPointerDown(e, ev) {
        if (ev.pointerType === 'mouse' && ev.button !== 0) {
            return;
        }
        // Bỏ các ngón "kẹt" (không còn được giữ, ví dụ mất sự kiện nhấc tay) để trình chỉnh không bị khóa.
        for (const id of [...e.pointers.keys()]) {
            if (!e.viewport.hasPointerCapture(id)) {
                e.pointers.delete(id);
            }
        }
        if (e.pointers.size >= 2) {
            return;
        }
        ev.preventDefault();
        e.viewport.setPointerCapture(ev.pointerId);
        e.pointers.set(ev.pointerId, { x: ev.clientX, y: ev.clientY });
        e.viewport.classList.add('is-dragging');
        beginGesture(e);
    }

    function onPointerMove(e, ev) {
        const p = e.pointers.get(ev.pointerId);
        if (!p) {
            return;
        }
        p.x = ev.clientX;
        p.y = ev.clientY;
        updateGesture(e);
    }

    function onPointerEnd(e, ev) {
        if (!e.pointers.delete(ev.pointerId)) {
            return;
        }
        if (e.viewport.hasPointerCapture(ev.pointerId)) {
            e.viewport.releasePointerCapture(ev.pointerId);
        }
        if (e.pointers.size > 0) {
            // Nhấc một ngón khi đang chụm: tiếp tục kéo bằng ngón còn lại từ vị trí hiện tại.
            beginGesture(e);
            notify(e, false);
            return;
        }
        e.gesture = null;
        e.viewport.classList.remove('is-dragging');
        render(e);
        notify(e, true);
    }

    function onWheel(e, ev) {
        ev.preventDefault();
        e.state.zoom = clamp(e.state.zoom * Math.exp(-ev.deltaY * WHEEL_ZOOM_SPEED), e.state.minZoom, e.state.maxZoom);
        scheduleRender(e);
        notify(e, false);
    }

    function attach(viewport, image, dotnet, options) {
        const id = nextId++;
        const e = {
            viewport,
            image,
            dotnet,
            state: { ...options },
            pointers: new Map(),
            gesture: null,
            frame: 0,
            notifyTimer: 0,
            handlers: {},
        };
        e.handlers.pointerdown = ev => onPointerDown(e, ev);
        e.handlers.pointermove = ev => onPointerMove(e, ev);
        e.handlers.pointerup = ev => onPointerEnd(e, ev);
        e.handlers.pointercancel = ev => onPointerEnd(e, ev);
        e.handlers.lostpointercapture = ev => onPointerEnd(e, ev);
        e.handlers.wheel = ev => onWheel(e, ev);
        for (const [name, handler] of Object.entries(e.handlers)) {
            viewport.addEventListener(name, handler, name === 'wheel' ? { passive: false } : undefined);
        }
        // Đổi kích thước cửa sổ: chỉ vẽ lại; trạng thái chuẩn hóa không đổi.
        e.resizeObserver = new ResizeObserver(() => scheduleRender(e));
        e.resizeObserver.observe(viewport);
        editors.set(id, e);
        render(e);
        return { id, zoom: e.state.zoom, fx: e.state.fx, fy: e.state.fy };
    }

    // Đặt trạng thái từ .NET (thanh trượt zoom, "Khôi phục Auto Crop"); trả về trạng thái chuẩn tắc.
    function setState(id, zoom, fx, fy) {
        const e = editors.get(id);
        if (!e) {
            return null;
        }
        const s = e.state;
        if (zoom !== null && zoom !== undefined) {
            s.zoom = zoom;
        }
        if (fx !== null && fx !== undefined) {
            s.fx = fx;
        }
        if (fy !== null && fy !== undefined) {
            s.fy = fy;
        }
        render(e);
        return { zoom: s.zoom, fx: s.fx, fy: s.fy };
    }

    function detach(id) {
        const e = editors.get(id);
        if (!e) {
            return;
        }
        editors.delete(id);
        for (const [name, handler] of Object.entries(e.handlers)) {
            e.viewport.removeEventListener(name, handler);
        }
        e.resizeObserver.disconnect();
        cancelAnimationFrame(e.frame);
        clearTimeout(e.notifyTimer);
        e.dotnet = null;
    }

    window.photoboothCropEditor = Object.freeze({ attach, setState, detach });
})();
