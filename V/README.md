# V Assist — chuột phải → chờ dao chém xong → mới bấm V

> **Game có anti-cheat (XIGNCODE…) hoặc bạn có source game?** Tool ngoài này sẽ bị chặn. Hãy dùng [Unity/](Unity/README.md): V chạy đúng frame nhát chém kết thúc, không cần đoán `delay_ms`, không có input giả.

Cùng kiến trúc với BhopAssist ở thư mục cha, chỉ khác việc nó làm. Build bằng `csc.exe` có sẵn trong Windows, không cần cài SDK.

| File | Là gì |
|---|---|
| `bin/VAssist.exe` | Tool **bên ngoài**: nghe chuột/phím bằng low-level hook, bấm V bằng `SendInput`. Không đọc/ghi bộ nhớ game, không inject DLL. |
| `bin/VTestbed.exe` | **Game thử**: mô phỏng nhát chém khoá hành động, để thử tool và nhìn thấy V đến sớm hay muộn trước khi vào game thật. |
| `bin/config.ini` | Cấu hình. Thông số chính là `delay_ms`. |

```powershell
.\build.ps1
.\bin\VAssist.exe                 # chạy
.\bin\VAssist.exe --watch         # tìm tên exe của game
.\bin\VAssist.exe --probe 60 680  # game 60 fps, nhát chém 680 ms -> V có đến sớm không?
.\bin\VAssist.exe --selftest      # đo độ chính xác, bấm F24 (vô hại) thay cho V
.\bin\VAssist.exe --admin         # khi game chạy quyền Administrator
.\bin\VTestbed.exe
```

---

## 1. Nó làm gì

```
t = 0           bạn bấm chuột phải (tay thật)      -> dao bắt đầu chém
t = delay_ms    tool bấm V xuống                    -> lúc này dao đã chém xong
t = + hold_ms   tool nhả V
```

Mỗi cú click chuột phải sinh ra **đúng một** lần bấm V, trễ `delay_ms`. Không bao giờ bấm V ngay lúc click.

Khác với BhopAssist (bắn phím theo **nhịp lặp lại** khi bật), tool này là **một phát mỗi click**.

Tool không biết dao chém xong lúc nào — nó không đọc game, chỉ đếm đồng hồ từ lúc bạn click. Vì vậy `delay_ms` là con số bạn phải tune, và tool cho tune ngay trong game (mục 3).

---

## 2. Vì sao phải chờ, và chờ thêm bao nhiêu

Trong lúc dao đang chém, game khoá hành động: V bấm vào lúc đó **bị nuốt mất** (hoặc cắt ngang nhát chém). Đó là lý do V phải đến *sau* khi nhát chém xong.

Nhưng "sau" ở đây là **theo đồng hồ của game**, và game chỉ nhìn đồng hồ qua từng frame. Tool đếm giờ chính xác tới 0.1 ms, nhưng game chỉ thấy cú click ở frame kế tiếp, và thấy V cũng ở frame kế tiếp. Đo thật bằng `--probe`:

```
> VAssist.exe --probe 60 680          (delay_ms = 700)

 2) Game thay V cach click bao lau?
    dung 700 ms (mo phong 20000 lan, frame time dao dong +-15 %):
       678..684 ms    #                           0.6 %
       684..690 ms    #####                       8.8 %
       690..696 ms    ############               22.0 %
       696..702 ms    #################          32.6 %
       702..708 ms    ############               23.5 %
       708..714 ms    ######                     11.3 %
       714..720 ms    #                           1.2 %
    => som nhat 681.3 ms, tre nhat 718.0 ms: sai so ~ +-1 frame.

 3) Neu nhat chem dai 680 ms:
    V den TRUOC khi chem xong :   0.0 %
    V den sau, tre trung binh :  19.4 ms
    => delay_ms an toan nho nhat: ~699 ms  (= chem + 19 ms, ~1.1 frame)
```

Hai điều rút ra:

- **`delay_ms` = thời gian chém + khoảng 1 frame.** Ở 60 fps là +17–19 ms. Đặt đúng bằng thời gian chém thì khoảng một nửa số lần V rơi vào frame cuối của nhát chém và bị nuốt.
- **Chỉnh từng bước nhỏ hơn 1 frame gần như không khác gì.** Game ở 60 fps không phân biệt được 700 ms với 705 ms. Bước mặc định 10 ms là vừa.

`hold_ms` cũng phải dài hơn 1 frame, vì cùng lý do như ở BhopAssist: game Unity đọc `Input.GetKey` một lần mỗi frame, bấm xuống rồi nhả gọn giữa hai frame là game không hề biết. Mặc định 50 ms an toàn tới 20 fps.

---

## 3. Tìm `delay_ms` — tune ngay trong game

Không cần thoát game để sửa config:

| Phím | Việc |
|---|---|
| `NUMPLUS` (dấu + bàn phím số) | `delay_ms` + 10 ms. Giữ phím thì nó chạy liên tục. |
| `NUMMINUS` (dấu −) | `delay_ms` − 10 ms |

Mỗi lần chỉnh, tool in số mới ra console, đổi tiêu đề cửa sổ, và **ghi luôn vào dòng `delay_ms` trong `config.ini`** — lần sau mở lên vẫn là số đó (tắt bằng `save_tuning = false`).

Cách tìm nhanh nhất:

1. Bắt đầu từ mặc định 700 ms (dư, nhưng chắc chắn ăn).
2. Chém thử, bấm `NUMMINUS` dần cho tới khi **thỉnh thoảng** V không ăn.
3. Bấm `NUMPLUS` lại 2 nấc (~20 ms ≈ 1 frame). Đó là số của bạn.

Laptop không có bàn phím số thì đổi `delay_up_key` / `delay_down_key` sang `PAGEUP` / `PAGEDOWN` hoặc `F9` / `F10`.

---

## 4. Quy trình cho game thật

**Bước 1 — tên exe.** `VAssist.exe --watch`, alt-tab sang game. Config đi kèm đã điền sẵn `SSJJ_BattleClient_Unity` (lấy từ config của BhopAssist), dòng `khop : CO` là đúng.

**Bước 2 — thử trên testbed.** Mở `VTestbed.exe` và `VAssist.exe`, click chuột phải vào cửa sổ testbed. Phải thấy chữ **ĂN** màu xanh và dòng `click -> V 700.x ms`. Thấy **KHONG CO V** thì tool chưa bắn vào được (xem mục 9).

**Bước 3 — vào game, tune** như mục 3.

**Bước 4 — quyền.** Nếu `--watch` báo "KHONG doc duoc", game chạy quyền Administrator. Khi đó Windows chặn **cả hai chiều**, im lặng: hook không nghe được chuột phải trong game, và `SendInput` bị từ chối. Chạy `VAssist.exe --admin`.

---

## 5. Click lại giữa chừng, giữ chuột

### `reclick` — click chuột phải lần nữa trong lúc V đang chờ

| Giá trị | V đến lúc nào | Khi nào dùng |
|---|---|---|
| `restart` *(mặc định)* | `delay_ms` sau click **cuối cùng** | An toàn nhất: dù bạn click đôi, click ba, V không bao giờ đến sớm. Đổi lại, có thể đến muộn hơn cần thiết. |
| `ignore` | `delay_ms` sau click **đầu tiên** | Nhanh hơn, nếu game **bỏ qua** click trong lúc dao đang chém (phần lớn game đều vậy). |

Testbed mô phỏng đúng kiểu "game bỏ qua click khi đang chém", nên bạn thấy được khác biệt: click đôi với `restart` thì V trễ thêm đúng khoảng cách giữa hai click.

### `start_from` — đếm giờ từ lúc nào

- `down` *(mặc định)*: từ lúc **nhấn** chuột phải. Hợp với game chém ngay khi nhấn.
- `up`: từ lúc **nhả** chuột phải. Dùng nếu bạn hay **giữ** chuột phải cho dao chém liên tục — V chỉ đến sau khi buông tay.

---

## 6. Chuột phải cũng là ngắm súng

Cầm súng, chuột phải là ngắm. Tool vẫn bấm V sau mỗi lần ngắm, vì nó không biết bạn đang cầm gì.

- **`toggle_key = F8`** — bấm để BẬT/TẮT. Trạng thái hiện ở tiêu đề cửa sổ: `[BAT]` / `[tat]`. Tắt trong lúc V đang chờ thì V đó bị huỷ luôn.
- **Tự động theo phím đổi vũ khí** (mặc định tắt):

  ```ini
  arm_keys    = 3          # dao ở phím 3 -> bấm 3 là BẬT
  disarm_keys = 1, 2, 4, 5 # đổi sang súng/lựu -> TẮT
  ```

  Chỉ tính khi đang ở trong game: gõ "2" trong Discord không tắt tool. Hạn chế: đổi vũ khí bằng **lăn chuột** hoặc **Q** (vũ khí trước) thì tool không biết.

---

## 7. Những chỗ dễ sai mà code đã xử lý

- **Click ở cửa sổ khác** (Discord, desktop) không sinh ra V. **Alt-tab ra trong lúc V đang chờ** thì V bị huỷ, không bấm vào cửa sổ khác.
- **Thread hook không bao giờ ghi console.** Console bị treo khi bạn bôi đen chữ trong cửa sổ console, mà hook treo quá ~300 ms là Windows gỡ hook ra. Mọi dòng log và việc ghi config đi qua hàng đợi, một thread riêng làm.
- **Hook chuột bỏ qua `WM_MOUSEMOVE` ngay từ mã message**, không đọc struct, không cấp phát. Chuột gaming báo vị trí 1000 lần/giây; hook chậm là chuột trong game trễ theo.
- **Lọc auto-repeat** của bàn phím: giữ `F8` không làm tool bật/tắt liên tục.
- **Thoát an toàn:** F12 hay Ctrl+C trong lúc đang giữ V → gỡ hook trước, chờ thread bắn nhả V ra rồi mới thoát. Process chết giữa chừng thì V kẹt ở trạng thái đang giữ.
- **Chữ ký riêng** trong `dwExtraInfo`, khác BhopAssist — chạy hai tool cùng lúc vẫn phân biệt được input của nhau.

`--selftest` chạy đúng thread bắn và bộ lịch thật, chỉ thay click bằng lệnh gọi trực tiếp và V bằng F24:

```
 [1] 6 click don, cach nhau ngau nhien
     F24 sau click : tb 700.19 ms   sd 0.42   min 700.00   max 701.13   (dat 700)
     giu F24       : tb 50.00 ms   min 50.00   max 50.01   (dat 50)
 [2] click lai sau 350 ms, reclick = restart
     ban 1 lan, +700.00 ms sau click THU HAI  -> DUNG
 [2] click lai sau 350 ms, reclick = ignore
     ban 1 lan, +700.00 ms sau click DAU TIEN  -> DUNG
 [3] tat tool trong luc V dang cho
     khong ban gi  -> DUNG
 [4] doi delay_ms 700 -> 730 trong luc dang cho (tune nong)
     ban o +730.00 ms  -> DUNG, theo so moi
```

Sai số của tool dưới 1 ms. Sai số còn lại (±1 frame) nằm ở game, xem mục 2.

---

## 8. Testbed

Mở `VTestbed.exe`, click chuột phải vào cửa sổ:

- Thanh cam = dao đang chém. V rơi vào vùng cam là **SỚM** (đỏ, bị nuốt). V sau vạch cam là **ĂN** (xanh), kèm số ms trễ.
- `UP` / `DOWN` chỉnh độ dài nhát chém ±10 ms (`PGUP` / `PGDN` ±50) cho giống dao trong game.
- `F5` chuyển LATCH ↔ POLL, `F7` đổi fps. Ở POLL, click và V chỉ được thấy ở frame kế tiếp (vạch nhỏ dưới thanh) — đúng cái sai số ±1 frame ở mục 2. Đặt `hold_ms = 5` trong config rồi thử, bạn sẽ thấy **BỎ SÓT**.
- Tự bấm V bằng tay cũng được, để so tay bạn với tool.

---

## 9. Chạy cùng BhopAssist

Hai tool không giẫm chân nhau:

| | BhopAssist | VAssist |
|---|---|---|
| Kích hoạt | `SPACE` (toggle) | chuột phải |
| Bắn ra | lăn chuột / SPACE | V |
| Bật/tắt | `SPACE` | `F8` |
| Thoát | `F12` | `F12` — một phím tắt cả hai |

---

## 10. Tham số

| Thông số | Ý nghĩa |
|---|---|
| `trigger_key` | Nút bắt đầu đếm giờ. `MOUSE2` = chuột phải. Nhận `MOUSE1..5` hoặc phím. |
| `start_from` | `down` = từ lúc nhấn, `up` = từ lúc nhả. Mục 5. |
| `delay_ms` | **Thông số chính.** Chờ bao lâu rồi mới bấm V. = thời gian chém + ~1 frame. |
| `reclick` | `restart` / `ignore`. Mục 5. |
| `emit_key` | Phím bấm ra. Mặc định `V`. |
| `hold_ms` | Giữ V bao lâu. Phải > 1 frame. Mặc định 50. |
| `press_count`, `press_gap_ms` | Bấm V mấy lần, nghỉ giữa các lần. Khoảng nghỉ cũng phải > 1 frame. |
| `send_mode` | `scancode` / `vk` / `both`. Thử lần lượt nếu game không nhận V. |
| `toggle_key`, `start_armed` | Phím bật/tắt, và trạng thái lúc mở tool. |
| `arm_keys`, `disarm_keys` | Tự bật/tắt theo phím đổi vũ khí. Mục 6. |
| `panic_key` | Thoát ngay, nhả hết phím. |
| `delay_up_key`, `delay_down_key`, `delay_step_ms`, `save_tuning` | Tune nóng. Mục 3. |
| `game_fps` | Fps **thấp nhất** trong game. Chỉ dùng để cảnh báo và làm mặc định cho `--probe`. |
| `target_process`, `require_focus` | Chốt an toàn: chỉ bấm V khi cửa sổ game đang focus. |
| `trigger_source` | `auto` = hook, tự rơi về `GetAsyncKeyState` nếu hook bị từ chối. |
| `accept_injected` | `true` = tin cả click do phần mềm sinh ra (vd phần mềm chuột remap nút). |

---

## 11. Khi V không ăn

| Triệu chứng | Nguyên nhân | Xử lý |
|---|---|---|
| Click trong game mà console không in dòng nào | Game chạy Administrator: Windows không cho tool nghe chuột trong cửa sổ đó (và cũng chặn `SendInput`) | `VAssist.exe --admin` |
| Console báo `SendInput bi UIPI chan (error 5)` | Như trên | `--admin` |
| In `bo qua click: dang focus '...'` | `target_process` sai tên | `--watch`, chép lại tên |
| In `V +700.0 ms` đều đặn, V **thỉnh thoảng** không ăn | `delay_ms` sát quá, rơi vào frame cuối nhát chém | `NUMPLUS` 2 nấc (+1 frame) |
| In `V +700.0 ms` đều đặn, V **không bao giờ** ăn | `delay_ms` ngắn hơn nhát chém, hoặc game cần giữ V lâu hơn | Tăng `delay_ms` mạnh tay (1000) cho chắc ăn rồi hạ dần; thử `hold_ms = 80` |
| Vẫn không | Game đọc scancode hay virtual-key? | `send_mode = scancode` → `vk` → `both` |

> Game online coi macro là gian lận. Tool này không che giấu gì cả — dùng ở đâu là tự chịu rủi ro khoá tài khoản.
