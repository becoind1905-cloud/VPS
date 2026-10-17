# Ekko Tools Auth Server

Chạy máy chủ:

```powershell
dotnet run --project src/EkkoBatchVideo.AuthServer --urls http://0.0.0.0:5080
```

Mở `http://localhost:5080/` để quản lý tài khoản. Đặt khóa quản trị trước khi chạy server:

```powershell
$env:EKKO_ADMIN_KEY = "một-khóa-bí-mật-dài"
```

Trong trang quản trị, nhập khóa rồi chọn cộng ngày, đặt ngày hết hạn hoặc khóa tài khoản. Không đưa trang quản trị ra Internet nếu chưa đặt khóa riêng và cấu hình HTTPS.

Sao lưu dữ liệu bằng cách gọi `GET /api/admin/backup` với header `X-Admin-Key` bằng khóa quản trị. Endpoint trả về file JSON để lưu trước khi chuyển VPS.

## Deploy Render

Đẩy repository lên GitHub, vào Render chọn **New → Web Service → Existing repository**, chọn runtime Docker. Có thể dùng file `render.yaml` ở thư mục gốc để Render tự nhận cấu hình. Tạo biến môi trường `EKKO_ADMIN_KEY` với một khóa bí mật dài, sau đó lấy URL dạng `https://ekko-auth-server.onrender.com` đặt vào `EKKO_AUTH_SERVER` trên máy khách.

Render Free có filesystem tạm thời. Vì vậy trước khi dùng thật cần chuyển phần lưu tài khoản từ `accounts.json` sang PostgreSQL (Supabase/Neon hoặc Render Postgres trả phí); nếu giữ `accounts.json`, dữ liệu có thể mất khi service restart hoặc sleep.

Ứng dụng desktop mặc định dùng URL Render của dự án. Khi triển khai server khác, đặt biến môi trường `EKKO_AUTH_SERVER` trên máy khách.

Tài khoản đăng ký mới được dùng thử 1 tiếng tính từ lúc server tạo tài khoản. App gửi mã máy băm từ Windows MachineGuid và mã đăng nhập cũ; server không dùng IP để cấp thử. Mỗi máy chỉ đăng ký một lần, kể cả đổi IP, đổi tài khoản Windows, xoá tài khoản trong admin hoặc reset máy đăng nhập. Bản app cũ thiếu mã dùng thử sẽ nhận yêu cầu cập nhật khi đăng ký; đăng nhập tài khoản cũ vẫn hoạt động.

PostgreSQL tự tạo bảng `device_trial_claims` riêng với khoá duy nhất; việc giữ lượt thử và tạo tài khoản chạy trong cùng transaction để chặn đăng ký đồng thời. Giữ bảng này khi backup/chuyển VPS. Sao lưu riêng qua `GET /api/admin/backup-device-trials` (header `X-Admin-Key`); backup tài khoản cũ vẫn giữ định dạng cũ. Không xoá bảng này khi xoá tài khoản.

Chế độ local dùng `accounts.json` và `device-trials.json` trong thư mục `EKKO_DATA_DIR` (mặc định cạnh file chạy). Trên Render phải giữ `DATABASE_URL` PostgreSQL; filesystem tạm thời không bảo đảm lưu lịch sử dùng thử. Token phiên vẫn có hạn 30 ngày, quyền sử dụng được quyết định bởi `ExpiresAt` trên server.

Giới hạn: MachineGuid không phụ thuộc IP nhưng có thể bị thay đổi khi cài lại Windows, tạo máy ảo hoặc giả mã máy. Không thể cam kết chặn tuyệt đối với client đã bị sửa. Máy đã gắn với tài khoản cũ được ghi nhận qua mã đăng nhập cũ để không nhận thử lại khi cập nhật.
