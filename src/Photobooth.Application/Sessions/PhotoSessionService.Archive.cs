using Microsoft.Extensions.Logging;
using Photobooth.Application.Composition;
using Photobooth.Application.Printing;

namespace Photobooth.Application.Sessions;

// Lưu trữ lâu dài, phiên bản thành phẩm, mở lại/chỉnh sửa phiên cũ và yêu cầu in.
public sealed partial class PhotoSessionService
{
    /// <summary>Hai yêu cầu in giống hệt (cùng phiên bản, cùng số bản) trong khoảng này được coi là bấm trùng.</summary>
    private static readonly TimeSpan DuplicatePrintWindow = TimeSpan.FromSeconds(3);

    private readonly SemaphoreSlim _persistGate = new(1, 1);
    private readonly SemaphoreSlim _versionGate = new(1, 1);
    private readonly SemaphoreSlim _printGate = new(1, 1);
    private readonly Lock _backgroundGate = new();
    private Task _backgroundWork = Task.CompletedTask;

    // ---------------------------------------------------------------- Ghi metadata

    /// <summary>
    /// Ghi session.json của phiên hiện tại (ảnh chụp mới nhất của trạng thái). Lỗi ghi không làm mất ảnh đã lưu;
    /// phiên được đánh dấu <see cref="PhotoSession.PersistenceFailed"/> và lần ghi sau sẽ ghi lại toàn bộ.
    /// </summary>
    /// <returns>true nếu đã ghi thành công (hoặc phiên không cần lưu).</returns>
    private async Task<bool> PersistCurrentAsync(CancellationToken cancellationToken)
    {
        await _persistGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Lấy trạng thái SAU khi vào hàng đợi để lần ghi sau luôn chứa dữ liệu mới nhất.
            var snapshot = Current;
            if (snapshot?.Directory is null)
            {
                return true;
            }

            var ok = await SaveSnapshotAsync(snapshot, cancellationToken).ConfigureAwait(false);
            Mutate(s => s.SessionId == snapshot.SessionId && s.PersistenceFailed == ok ? s with { PersistenceFailed = !ok } : null);
            return ok;
        }
        finally
        {
            _persistGate.Release();
        }
    }

    private async Task<bool> SaveSnapshotAsync(PhotoSession snapshot, CancellationToken cancellationToken)
    {
        try
        {
            await _archive.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Không ghi được dữ liệu phiên {SessionId}", snapshot.SessionId);
            return false;
        }
    }

    /// <summary>Sau khi một phiên kết thúc: ghi trạng thái cuối và dọn file nháp (chạy nền, có thể chờ bằng <see cref="FlushAsync"/>).</summary>
    private void AfterSessionEnded(PhotoSession? ended)
    {
        if (ended?.Directory is null)
        {
            return;
        }

        QueueBackground(async () =>
        {
            if (!ended.IsArchived)
            {
                await _persistGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    await SaveSnapshotAsync(ended, CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    _persistGate.Release();
                }
            }

            await DeleteDraftsSafelyAsync(ended).ConfigureAwait(false);
        });
    }

    private void QueueBackground(Func<Task> work)
    {
        lock (_backgroundGate)
        {
            _backgroundWork = _backgroundWork.ContinueWith(_ => work(), TaskScheduler.Default).Unwrap();
        }
    }

    /// <summary>Chờ các thao tác ghi nền hoàn tất (gọi khi đóng ứng dụng).</summary>
    public Task FlushAsync()
    {
        lock (_backgroundGate)
        {
            return _backgroundWork;
        }
    }

    private async Task DeleteDraftsSafelyAsync(PhotoSession session)
    {
        try
        {
            await _archive.DeleteDraftsAsync(session).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Không dọn được file nháp của phiên {SessionId}", session.SessionId);
        }
    }

    // ---------------------------------------------------------------- Phiên bản thành phẩm

    /// <summary>
    /// Xác nhận ảnh thành phẩm đang hiển thị thành một phiên bản lưu trong Event Workspace (<c>final-v{N}.png</c>).
    /// Nếu đã có phiên bản tạo từ đúng các đầu vào này thì dùng lại — không tạo bản trùng. Không bao giờ ghi đè phiên bản cũ.
    /// </summary>
    /// <returns>Phiên bản đã lưu; null nếu chưa có thành phẩm hoặc ghi file thất bại.</returns>
    public async Task<OutputVersion?> ConfirmOutputAsync(CancellationToken cancellationToken = default)
    {
        await _versionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PhotoSession session;
            ComposedResult composed;
            lock (_gate)
            {
                if (_current is not { State: PhotoSessionState.Ready, Composed: { } current, Directory: not null } active)
                {
                    return null;
                }

                session = active;
                composed = current;
            }

            if (session.Versions.LastOrDefault(v => v.Matches(composed)) is { } existing && File.Exists(existing.FilePath))
            {
                return existing;
            }

            var next = (session.Versions.Count == 0 ? 0 : session.Versions.Max(v => v.Version)) + 1;
            OutputVersion created;
            try
            {
                created = await _archive.CreateOutputVersionAsync(session, composed, next, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "Không lưu được phiên bản thành phẩm v{Version} của phiên {SessionId}", next, session.SessionId);
                return null;
            }

            // Phiên mở lại (Admin): chuyển sang hiển thị chính file phiên bản vừa lưu, để bản nháp được dọn.
            Mutate(s => s.SessionId != session.SessionId
                ? null
                : s.IsArchived
                    ? RestoreFromVersion(s with { Versions = [.. s.Versions, created] }, created)
                    : s with { Versions = [.. s.Versions, created] });
            _logger.LogInformation("Đã lưu thành phẩm v{Version} cho phiên {SessionId}", created.Version, session.SessionId);
            await PersistCurrentAsync(CancellationToken.None).ConfigureAwait(false);
            return created;
        }
        finally
        {
            _versionGate.Release();
        }
    }

    // ---------------------------------------------------------------- Mở lại phiên đã lưu (Admin)

    /// <summary>
    /// Mở một phiên đã lưu để xem/chỉnh sửa/in lại. Không được mở khi đang có phiên Kiosk chưa kết thúc.
    /// Trạng thái chỉnh sửa (ảnh nguồn, Manual Crop, bộ lọc) được khôi phục từ phiên bản mới nhất.
    /// </summary>
    public bool OpenArchivedSession(ArchivedSession archived)
    {
        ArgumentNullException.ThrowIfNull(archived);
        PhotoSession? previous;
        lock (_gate)
        {
            if (_current is { IsArchived: false, IsEnded: false })
            {
                return false;
            }

            previous = _current?.IsArchived == true ? _current : null;
            var session = archived.Session with { IsArchived = true };
            _current = session.LatestVersion is { } latest ? RestoreFromVersion(session, latest) : session;
        }

        AfterSessionEnded(previous);
        _logger.LogInformation("Mở lại phiên đã lưu {SessionId}", archived.Session.SessionId);
        RaiseSessionChanged();
        return true;
    }

    /// <summary>Bắt đầu chỉnh sửa từ một phiên bản cụ thể (khôi phục đúng crop/bộ lọc/ảnh nguồn của phiên bản đó).</summary>
    public bool BeginEditFromVersion(int version) =>
        TryTransition(
            s => s.IsArchived && s.CanEdit && s.State is PhotoSessionState.Ready or PhotoSessionState.Reviewing && s.FindVersion(version) is not null,
            s => RestoreFromVersion(s, s.FindVersion(version)!));

    /// <summary>Hủy chỉnh sửa: quay về phiên bản gốc, không tạo phiên bản mới, không đổi dữ liệu đã xác nhận.</summary>
    public bool DiscardEdits()
    {
        var discarded = TryTransition(
            s => s.IsArchived && s.State is PhotoSessionState.Ready or PhotoSessionState.Reviewing,
            s => s.EditBaseVersion is { } baseVersion && s.FindVersion(baseVersion) is { } v
                ? RestoreFromVersion(s, v)
                : s with { Composed = null, SlotCrops = new SlotCrop?[s.SlotCrops.Count], Filter = ColorFilter.None, State = PhotoSessionState.Reviewing });

        if (discarded && Current is { } session)
        {
            QueueBackground(() => DeleteDraftsSafelyAsync(session));
        }

        return discarded;
    }

    /// <summary>Lưu chỉnh sửa của phiên đã mở lại thành phiên bản mới (phiên bản cũ được giữ nguyên).</summary>
    public async Task<OutputVersion?> SaveEditsAsync(CancellationToken cancellationToken = default)
    {
        if (Current is not { IsArchived: true, CanEdit: true })
        {
            return null;
        }

        var saved = await ConfirmOutputAsync(cancellationToken).ConfigureAwait(false);
        if (saved is not null && Current is { } session)
        {
            QueueBackground(() => DeleteDraftsSafelyAsync(session));
        }

        return saved;
    }

    /// <summary>Ảnh xem trước của một phiên bản đã lưu (đúng file sẽ được xuất/in, không render lại).</summary>
    public async Task<byte[]?> GetVersionPreviewAsync(int version, int maxWidthPixels, CancellationToken cancellationToken = default)
    {
        if (Current?.FindVersion(version) is not { } output || !File.Exists(output.FilePath))
        {
            return null;
        }

        return await _previewRenderer.RenderPreviewAsync(output.FilePath, mirror: false, maxWidthPixels, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Khôi phục ảnh nguồn, Manual Crop, bộ lọc và thành phẩm của một phiên bản đã lưu.</summary>
    private static PhotoSession RestoreFromVersion(PhotoSession s, OutputVersion v)
    {
        var slots = v.PhotoIds.Select(id => s.Photos.FirstOrDefault(p => p.PhotoId == id)).ToArray();
        var slotCount = s.Template?.Slots.Count ?? v.SlotCrops.Count;
        var crops = Enumerable.Range(0, slotCount).Select(i => i < v.SlotCrops.Count ? v.SlotCrops[i] : null).ToArray();
        return s with
        {
            State = PhotoSessionState.Ready,
            Slots = slots,
            SlotCrops = crops,
            Filter = v.Filter,
            Composed = new ComposedResult(v.FilePath, v.WidthPixels, v.HeightPixels, v.TemplateId, v.Filter, v.PhotoIds, v.CreatedAt, crops),
            LastCompositionFailed = false,
            EditBaseVersion = v.Version,
        };
    }

    // ---------------------------------------------------------------- In ảnh

    /// <summary>
    /// Tạo yêu cầu in <paramref name="copies"/> bản từ MỘT ảnh thành phẩm đã lưu (số bản không liên quan số ảnh nguồn).
    /// <paramref name="version"/> = null: xác nhận/dùng phiên bản đang xem; ngược lại in lại đúng phiên bản đã chỉ định.
    /// Print Job được ghi (Pending) TRƯỚC khi gửi máy in để không có lệnh in nào không được ghi nhận.
    /// Không bao giờ tự gửi lại: phiên bản có lệnh in không rõ trạng thái/thất bại cần <paramref name="confirmRetry"/>.
    /// </summary>
    public async Task<PrintResult> RequestPrintAsync(int copies, int? version = null, bool confirmRetry = false, CancellationToken cancellationToken = default)
    {
        if (copies < 1 || copies > _printOptions.MaxCopies)
        {
            return new PrintResult(PrintOutcome.InvalidCopies);
        }

        if (_printers.Current is not { } printer)
        {
            return new PrintResult(PrintOutcome.NotConfigured);
        }

        if (!await _printGate.WaitAsync(0, CancellationToken.None).ConfigureAwait(false))
        {
            return new PrintResult(PrintOutcome.Busy);
        }

        try
        {
            var output = version is { } requested
                ? Current?.FindVersion(requested)
                : await ConfirmOutputAsync(cancellationToken).ConfigureAwait(false);
            if (output is null || !File.Exists(output.FilePath) || Current is not { } session)
            {
                return new PrintResult(PrintOutcome.NothingToPrint);
            }

            var now = _timeProvider.GetUtcNow();
            var previous = session.PrintJobs.Where(j => j.OutputVersion == output.Version).ToArray();
            if (!confirmRetry && previous.LastOrDefault(j => j.Status is PrintJobStatus.Pending or PrintJobStatus.Failed) is { } unclear)
            {
                return new PrintResult(PrintOutcome.ConfirmationRequired, unclear);
            }

            // Bấm trùng: cùng phiên bản, cùng số bản, vừa gửi xong (lệnh đã hủy/thất bại không tính).
            if (previous.LastOrDefault(j => j.Status is not (PrintJobStatus.Cancelled or PrintJobStatus.Failed)) is { } last
                && last.Copies == copies
                && now - last.CreatedAt < DuplicatePrintWindow)
            {
                _logger.LogWarning("Bỏ qua yêu cầu in trùng cho phiên {SessionId} v{Version}", session.SessionId, output.Version);
                return new PrintResult(PrintOutcome.Busy, last);
            }

            var job = new PrintJob(Guid.NewGuid(), session.SessionId, output.Version, output.FilePath, copies, printer.Name, printer.IsPhysical, PrintJobStatus.Pending, now);
            UpsertPrintJob(job);
            if (!await PersistCurrentAsync(cancellationToken).ConfigureAwait(false))
            {
                // Không ghi được lịch sử → không gửi lệnh in (tránh in mà không có dấu vết).
                job = job with { Status = PrintJobStatus.Failed, UpdatedAt = _timeProvider.GetUtcNow(), ErrorMessage = "Không ghi được lịch sử in vào thư mục sự kiện." };
                UpsertPrintJob(job);
                return new PrintResult(PrintOutcome.Failed, job, job.ErrorMessage);
            }

            PrintSubmission submission;
            try
            {
                submission = await printer
                    .SubmitAsync(new PrintRequest(job.PrintJobId, output.FilePath, copies, output.WidthPixels, output.HeightPixels, output.Dpi), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                submission = new PrintSubmission(PrintJobStatus.Failed, "Đã hủy trước khi gửi lệnh in.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
            {
                _logger.LogError(ex, "Gửi lệnh in thất bại ({Printer})", printer.Name);
                submission = new PrintSubmission(PrintJobStatus.Failed, "Không gửi được lệnh in tới máy in.");
            }

            // Máy in chỉ được báo "đã tiếp nhận"/"giả lập"/"lỗi"; "in xong" luôn do người xác nhận.
            var status = submission.Status switch
            {
                PrintJobStatus.Submitted when printer.IsPhysical => PrintJobStatus.Submitted,
                PrintJobStatus.Submitted or PrintJobStatus.Simulated or PrintJobStatus.Completed when !printer.IsPhysical => PrintJobStatus.Simulated,
                PrintJobStatus.Completed => PrintJobStatus.Submitted,
                _ => PrintJobStatus.Failed,
            };
            job = job with { Status = status, UpdatedAt = _timeProvider.GetUtcNow(), ErrorMessage = status == PrintJobStatus.Failed ? submission.ErrorMessage ?? "Lỗi không xác định." : null };
            UpsertPrintJob(job);
            await PersistCurrentAsync(CancellationToken.None).ConfigureAwait(false);

            _logger.LogInformation(
                "Yêu cầu in {Copies} bản phiên {SessionId} v{Version} qua {Printer}: {Status}",
                copies, session.SessionId, output.Version, printer.Name, status);

            var outcome = status switch
            {
                PrintJobStatus.Submitted => PrintOutcome.Submitted,
                PrintJobStatus.Simulated => PrintOutcome.Simulated,
                _ => PrintOutcome.Failed,
            };
            return new PrintResult(outcome, job, job.ErrorMessage);
        }
        finally
        {
            _printGate.Release();
        }
    }

    /// <summary>
    /// Quản trị viên cập nhật trạng thái thực tế: Submitted → Completed (đã kiểm tra bản in) hoặc Failed;
    /// Pending/Failed → Cancelled (xác nhận lệnh không rõ trạng thái sẽ không in lại).
    /// </summary>
    public async Task<bool> UpdatePrintJobStatusAsync(Guid printJobId, PrintJobStatus status, CancellationToken cancellationToken = default)
    {
        var job = Current?.PrintJobs.FirstOrDefault(j => j.PrintJobId == printJobId);
        var allowed = (job?.Status, status) switch
        {
            (PrintJobStatus.Submitted, PrintJobStatus.Completed) => true,
            (PrintJobStatus.Submitted, PrintJobStatus.Failed) => true,
            (PrintJobStatus.Pending or PrintJobStatus.Failed, PrintJobStatus.Cancelled) => true,
            _ => false,
        };

        if (!allowed || job is null)
        {
            return false;
        }

        UpsertPrintJob(job with { Status = status, UpdatedAt = _timeProvider.GetUtcNow() });
        return await PersistCurrentAsync(cancellationToken).ConfigureAwait(false);
    }

    private void UpsertPrintJob(PrintJob job) =>
        Mutate(s => s.SessionId != job.SessionId
            ? null
            : s with
            {
                PrintJobs = s.PrintJobs.Any(j => j.PrintJobId == job.PrintJobId)
                    ? s.PrintJobs.Select(j => j.PrintJobId == job.PrintJobId ? job : j).ToArray()
                    : [.. s.PrintJobs, job],
            });
}
