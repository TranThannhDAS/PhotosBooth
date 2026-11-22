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

            candidate = await navigator.mediaDevices.getUserMedia({
                audio: false,
                video: {
                    width: { ideal: options.idealWidth },
                    height: { ideal: options.idealHeight },
                    facingMode: 'user'
                }
            });

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

            return { ok: true, width: videoElement.videoWidth, height: videoElement.videoHeight };
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

    window.photoboothCamera = Object.freeze({ start, stop, capture });

    // Phòng hờ: giải phóng camera khi trang bị gỡ (tải lại giao diện, đóng cửa sổ).
    window.addEventListener('pagehide', stop);
})();
