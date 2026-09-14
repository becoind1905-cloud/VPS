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

Tài khoản mới được cấp 30 ngày. Dữ liệu được lưu trong `accounts.json` cạnh file chạy server. Token phiên có hạn 30 ngày; các API nghiệp vụ nên yêu cầu `Authorization: Bearer <token>` và kiểm tra `ExpiresAt` như endpoint `/api/protected/ping` mẫu.
