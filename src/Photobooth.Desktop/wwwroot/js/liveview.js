// Hiển thị Live View máy ảnh Canon: lấy từng khung JPEG (nhị phân) từ endpoint nội bộ rồi vẽ lên <canvas>.
// Chỉ xin khung tiếp theo sau khi vẽ xong khung trước → tự điều tiết theo tốc độ máy ảnh/giao diện.
(() => {
    'use strict';

    let session = 0;

    const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

    async function run(mySession, canvas, url, fpsElement) {
        const context = canvas.getContext('2d');
        let after = 0;
        let frames = 0;
        let windowStart = performance.now();

        while (mySession === session) {
            try {
                const response = await fetch(`${url}?after=${after}`, { cache: 'no-store' });
                if (mySession !== session) {
                    break;
                }

                if (response.status === 200) {
                    after = Number(response.headers.get('X-Frame-Sequence')) || after;
                    const bitmap = await createImageBitmap(await response.blob());
                    if (mySession !== session) {
                        bitmap.close();
                        break;
                    }
                    if (canvas.width !== bitmap.width || canvas.height !== bitmap.height) {
                        canvas.width = bitmap.width;
                        canvas.height = bitmap.height;
                    }
                    context.drawImage(bitmap, 0, 0);
                    bitmap.close();
                    frames++;
                } else if (response.status !== 204) {
                    await sleep(250);
                }
            } catch {
                // Endpoint chưa sẵn sàng hoặc khung hỏng: thử lại sau, không dừng hiển thị.
                await sleep(250);
            }

            const now = performance.now();
            if (fpsElement && now - windowStart >= 1000) {
                fpsElement.textContent = `${Math.round(frames * 1000 / (now - windowStart))} khung/giây`;
                frames = 0;
                windowStart = now;
            }
        }
    }

    function start(canvas, url, fpsElement) {
        const mySession = ++session;
        run(mySession, canvas, url, fpsElement);
    }

    function stop(canvas, fpsElement) {
        session++;
        if (canvas) {
            const context = canvas.getContext('2d');
            context.clearRect(0, 0, canvas.width, canvas.height);
        }
        if (fpsElement) {
            fpsElement.textContent = '';
        }
    }

    window.photoboothLiveView = Object.freeze({ start, stop });
    window.addEventListener('pagehide', () => stop());
})();
