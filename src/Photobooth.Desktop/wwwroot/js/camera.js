// Cầu nối webcam cho WebView2: getUserMedia + <video> preview + chụp một khung hình JPEG.
// Chỉ chứa logic API trình duyệt; mọi nghiệp vụ phiên chụp nằm ở C#.
(() => {
    'use strict';

    let stream = null;
    let video = null;
    let dotnetRef = null;
    let onVideoResize = null;
    // Tăng mỗi lần start/stop để bỏ kết quả của lần start cũ đã bị thay thế.
    let generation = 0;

    function stopTracks(mediaStream) {
        if (!mediaStream) {
            return;
        }
        for (const track of mediaStream.getTracks()) {
            track.onended = null;
            track.stop();
        }
    }

    function detachVideo() {
        if (video && onVideoResize) {
            video.removeEventListener('resize', onVideoResize);
        }
        onVideoResize = null;
        if (video) {
            try {
                video.pause();
            } catch {
                // Bỏ qua: phần tử có thể đã bị gỡ khỏi DOM.
            }
            video.srcObject = null;
        }
        video = null;
    }

    function stop() {
        generation++;
        stopTracks(stream);
        stream = null;
        detachVideo();
        dotnetRef = null;
    }

    async function hasVideoInput() {
        if (!navigator.mediaDevices || !navigator.mediaDevices.enumerateDevices) {
            // Không xác định được: để getUserMedia trả lỗi cụ thể.
            return true;
        }
        const devices = await navigator.mediaDevices.enumerateDevices();
        return devices.some(d => d.kind === 'videoinput');
    }

    function isMissingDeviceError(err) {
        return !!err && (err.name === 'OverconstrainedError' || err.name === 'NotFoundError' || err.name === 'DevicesNotFoundError');
    }

    async function videoInputs() {
        const devices = await navigator.mediaDevices.enumerateDevices();
        return devices.filter(d => d.kind === 'videoinput');
    }

    // Tên/mã thiết bị chỉ có sau khi trang đã được cấp quyền camera: mở camera thật ngắn rồi tắt ngay.
    async function ensureDeviceLabels() {
        let inputs = await videoInputs();
        if (inputs.length > 0 && inputs.every(d => d.deviceId && d.label)) {
            return inputs;
        }
        const probe = await navigator.mediaDevices.getUserMedia({ audio: false, video: true });
        stopTracks(probe);
        return await videoInputs();
    }

    // Mở camera theo lựa chọn trong Admin. Không có lựa chọn → camera mặc định (như trước).
    // Mã thiết bị không còn khớp → tìm lại theo tên (chỉ khi đúng một camera trùng tên); không tìm thấy → lỗi riêng,
    // KHÔNG tự chuyển sang camera khác.
    async function openStream(options) {
        const size = { width: { ideal: options.idealWidth }, height: { ideal: options.idealHeight } };
        if (!options.deviceId) {
            const mediaStream = await navigator.mediaDevices.getUserMedia({ audio: false, video: { ...size, facingMode: 'user' } });
            return { mediaStream, resolvedDeviceId: null };
        }

        try {
            const mediaStream = await navigator.mediaDevices.getUserMedia({ audio: false, video: { ...size, deviceId: { exact: options.deviceId } } });
            return { mediaStream, resolvedDeviceId: options.deviceId };
        } catch (err) {
            if (!isMissingDeviceError(err)) {
                throw err;
            }
        }

        const matches = options.deviceLabel
            ? (await ensureDeviceLabels()).filter(d => d.label === options.deviceLabel)
            : [];
        if (matches.length !== 1) {
            throw Object.assign(new Error('Không tìm thấy camera đã chọn.'), { name: 'SelectedDeviceMissing' });
        }

        const resolvedDeviceId = matches[0].deviceId;
        const mediaStream = await navigator.mediaDevices.getUserMedia({ audio: false, video: { ...size, deviceId: { exact: resolvedDeviceId } } });
        return { mediaStream, resolvedDeviceId };
    }

    // Danh sách webcam cho Admin. requestLabels: được phép mở camera thoáng qua để lấy tên (chỉ khi chưa có camera nào đang chạy).
    async function listDevices(requestLabels) {
        if (!navigator.mediaDevices || !navigator.mediaDevices.enumerateDevices) {
            return { devices: [], errorName: 'NotSupported' };
        }

        let errorName = null;
        let inputs;
        try {
            inputs = requestLabels && !stream ? await ensureDeviceLabels() : await videoInputs();
        } catch (err) {
            errorName = (err && err.name) || 'Error';
            inputs = await videoInputs();
        }

        return {
            devices: inputs.filter(d => d.deviceId).map(d => ({ deviceId: d.deviceId, label: d.label || '' })),
            errorName
        };
    }

    function waitForVideoReady(videoElement) {
        if (videoElement.readyState >= 2 && videoElement.videoWidth > 0) {
            return Promise.resolve();
        }
        return new Promise((resolve, reject) => {
            const timeout = setTimeout(() => {
                cleanup();
                reject(Object.assign(new Error('Camera không trả về khung hình.'), { name: 'NotReadableError' }));
            }, 10000);
            const onReady = () => {
                if (videoElement.videoWidth > 0) {
                    cleanup();
                    resolve();
                }
            };
            function cleanup() {
                clearTimeout(timeout);
                videoElement.removeEventListener('loadeddata', onReady);
                videoElement.removeEventListener('resize', onReady);
            }
            videoElement.addEventListener('loadeddata', onReady);
            videoElement.addEventListener('resize', onReady);
        });
    }

    async function start(videoElement, callbackRef, options) {
        stop();
        const myGeneration = ++generation;

        if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
            return { ok: false, errorName: 'NotSupported' };
        }

        let candidate = null;
        try {
            if (!(await hasVideoInput())) {
                return { ok: false, errorName: 'NoDevice' };
            }

            const opened = await openStream(options);
            candidate = opened.mediaStream;
            const resolvedDeviceId = opened.resolvedDeviceId;

            if (myGeneration !== generation) {
                stopTracks(candidate);
                return { ok: false, errorName: 'Superseded' };
            }

            stream = candidate;
            video = videoElement;
            dotnetRef = callbackRef;

            const track = candidate.getVideoTracks()[0];
            if (track) {
                const owned = candidate;
                track.onended = () => {
                    // Thiết bị bị rút/ứng dụng khác chiếm quyền: báo về C# một lần.
                    if (stream !== owned) {
                        return;
                    }
                    const callback = dotnetRef;
                    stop();
                    if (callback) {
                        callback.invokeMethodAsync('OnCameraTrackEnded').catch(() => { });
                    }
                };
            }

            videoElement.muted = true;
            videoElement.playsInline = true;
            videoElement.srcObject = candidate;
            await videoElement.play();
            await waitForVideoReady(videoElement);

            if (myGeneration !== generation) {
                return { ok: false, errorName: 'Superseded' };
            }

            // Camera đổi độ phân giải/tỉ lệ khi đang chạy: báo C# để cập nhật khung hướng dẫn
            // (chỉ khi kích thước thay đổi, không chạy theo từng khung hình).
            onVideoResize = () => {
                const callback = dotnetRef;
                if (callback && stream === candidate && videoElement.videoWidth > 0) {
                    callback.invokeMethodAsync('OnVideoResized', videoElement.videoWidth, videoElement.videoHeight).catch(() => { });
                }
            };
            videoElement.addEventListener('resize', onVideoResize);

            const activeTrack = candidate.getVideoTracks()[0];
            return {
                ok: true,
                width: videoElement.videoWidth,
                height: videoElement.videoHeight,
                resolvedDeviceId,
                label: (activeTrack && activeTrack.label) || ''
            };
        } catch (err) {
            if (myGeneration !== generation) {
                // Đã có lệnh stop/start mới: chỉ dọn stream của lần start này.
                stopTracks(candidate);
                return { ok: false, errorName: 'Superseded' };
            }
            stop();
            return { ok: false, errorName: (err && err.name) || 'Error', message: String((err && err.message) || '') };
        }
    }

    async function capture(quality) {
        const track = stream ? stream.getVideoTracks()[0] : null;
        if (!stream || !video || !track || track.readyState !== 'live' || video.readyState < 2 || !video.videoWidth) {
            throw new Error('PB_NOT_ACTIVE');
        }

        const canvas = document.createElement('canvas');
        canvas.width = video.videoWidth;
        canvas.height = video.videoHeight;
        const context = canvas.getContext('2d');
        if (!context) {
            throw new Error('PB_ENCODE_FAILED');
        }

        // Vẽ khung hình gốc, KHÔNG lật gương (preview lật chỉ bằng CSS).
        context.drawImage(video, 0, 0, canvas.width, canvas.height);

        const blob = await new Promise((resolve, reject) => {
            canvas.toBlob(
                b => (b ? resolve(b) : reject(new Error('PB_ENCODE_FAILED'))),
                'image/jpeg',
                quality);
        });

        canvas.width = 0;
        canvas.height = 0;
        return new Uint8Array(await blob.arrayBuffer());
    }

    window.photoboothCamera = Object.freeze({ start, stop, capture, listDevices });

    // Phòng hờ: giải phóng camera khi trang bị gỡ (tải lại giao diện, đóng cửa sổ).
    window.addEventListener('pagehide', stop);
})();
