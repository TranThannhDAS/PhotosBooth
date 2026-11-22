// Visual Template Editor — chỉ chuyển thao tác chuột/cảm ứng (Pointer Events) và kích thước vùng xem sang .NET.
// KHÔNG tính tọa độ template ở đây: JS gửi độ dời theo CSS pixel kể từ lúc nhấn; C# đổi sang pixel canvas
// (CanvasGeometry.ToCanvasDelta) và quyết định vị trí/kích thước. Vì vậy zoom, DPI hay kích thước cửa sổ
// không làm sai tọa độ lưu trong template.json.
(() => {
    'use strict';

    const editors = new Map();
    let nextId = 1;

    function isTypingTarget(target) {
        return !!target && (target.closest('input, textarea, select, [contenteditable="true"]') !== null);
    }

    function attach(root, viewport, dotnetRef) {
        const id = nextId++;
        const state = { root, viewport, dotnetRef, drag: null, frame: 0, inFlight: false, pending: null };

        state.onPointerDown = (e) => {
            if (e.button !== 0 && e.pointerType === 'mouse') {
                return;
            }
            const element = e.target.closest('[data-key]');
            if (!element || !viewport.contains(element)) {
                if (e.target.closest('.tpl-canvas, .tpl-viewport')) {
                    dotnetRef.invokeMethodAsync('OnCanvasPointerDown', null, null);
                }
                return;
            }
            const handle = e.target.closest('[data-handle]');
            e.preventDefault();
            viewport.setPointerCapture(e.pointerId);
            state.drag = { pointerId: e.pointerId, x: e.clientX, y: e.clientY, dx: 0, dy: 0, shift: e.shiftKey };
            dotnetRef.invokeMethodAsync('OnCanvasPointerDown', element.dataset.key, handle ? handle.dataset.handle : 'Move');
        };

        // Gộp các lần di chuyển theo khung hình và không gửi chồng lời gọi: luôn gửi trạng thái mới nhất.
        const flush = async () => {
            state.frame = 0;
            if (state.inFlight || !state.pending) {
                return;
            }
            const p = state.pending;
            state.pending = null;
            state.inFlight = true;
            try {
                await dotnetRef.invokeMethodAsync('OnCanvasDrag', p.dx, p.dy, p.shift);
            } catch {
                // Component đã đóng.
            } finally {
                state.inFlight = false;
                if (state.pending && !state.frame) {
                    state.frame = requestAnimationFrame(flush);
                }
            }
        };

        state.onPointerMove = (e) => {
            const d = state.drag;
            if (!d || e.pointerId !== d.pointerId) {
                return;
            }
            d.dx = e.clientX - d.x;
            d.dy = e.clientY - d.y;
            d.shift = e.shiftKey;
            state.pending = { dx: d.dx, dy: d.dy, shift: d.shift };
            if (!state.frame) {
                state.frame = requestAnimationFrame(flush);
            }
        };

        state.onPointerUp = (e) => {
            const d = state.drag;
            if (!d || e.pointerId !== d.pointerId) {
                return;
            }
            state.drag = null;
            state.pending = null;
            if (state.frame) {
                cancelAnimationFrame(state.frame);
                state.frame = 0;
            }
            if (viewport.hasPointerCapture(e.pointerId)) {
                viewport.releasePointerCapture(e.pointerId);
            }
            const cancelled = e.type === 'pointercancel';
            dotnetRef.invokeMethodAsync('OnCanvasDragEnd', d.dx, d.dy, d.shift, cancelled).catch(() => { });
        };

        state.onWheel = (e) => {
            if (!e.ctrlKey) {
                return;
            }
            e.preventDefault();
            dotnetRef.invokeMethodAsync('OnZoomWheel', e.deltaY < 0 ? 1 : -1).catch(() => { });
        };

        state.onKeyDown = (e) => {
            if (isTypingTarget(e.target)) {
                return;
            }
            const ctrl = e.ctrlKey || e.metaKey;
            let command = null;
            if (ctrl && (e.key === 'z' || e.key === 'Z')) {
                command = e.shiftKey ? 'redo' : 'undo';
            } else if (ctrl && (e.key === 'y' || e.key === 'Y')) {
                command = 'redo';
            } else if (ctrl && (e.key === 'd' || e.key === 'D')) {
                command = 'duplicate';
            } else if (ctrl && (e.key === 's' || e.key === 'S')) {
                command = 'save';
            } else if (e.key === 'Delete') {
                command = 'delete';
            } else if (e.key === 'Escape') {
                command = 'deselect';
            } else if (e.key.startsWith('Arrow')) {
                command = e.key;
            }
            if (command) {
                e.preventDefault();
                dotnetRef.invokeMethodAsync('OnShortcut', command, e.shiftKey).catch(() => { });
            }
        };

        state.resizeObserver = new ResizeObserver(() => {
            dotnetRef.invokeMethodAsync('OnViewportResized', viewport.clientWidth, viewport.clientHeight).catch(() => { });
        });

        viewport.addEventListener('pointerdown', state.onPointerDown);
        viewport.addEventListener('pointermove', state.onPointerMove);
        viewport.addEventListener('pointerup', state.onPointerUp);
        viewport.addEventListener('pointercancel', state.onPointerUp);
        viewport.addEventListener('wheel', state.onWheel, { passive: false });
        root.addEventListener('keydown', state.onKeyDown);
        state.resizeObserver.observe(viewport);
        editors.set(id, state);
        return id;
    }

    function detach(id) {
        const state = editors.get(id);
        if (!state) {
            return;
        }
        editors.delete(id);
        if (state.frame) {
            cancelAnimationFrame(state.frame);
        }
        state.resizeObserver.disconnect();
        state.viewport.removeEventListener('pointerdown', state.onPointerDown);
        state.viewport.removeEventListener('pointermove', state.onPointerMove);
        state.viewport.removeEventListener('pointerup', state.onPointerUp);
        state.viewport.removeEventListener('pointercancel', state.onPointerUp);
        state.viewport.removeEventListener('wheel', state.onWheel);
        state.root.removeEventListener('keydown', state.onKeyDown);
    }

    window.photoboothTemplateEditor = Object.freeze({ attach, detach });
})();
