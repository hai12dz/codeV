# Bhop Assist — trợ lý input "làm ở ngoài"

Hai chương trình, build bằng `csc.exe` có sẵn trong Windows, không cần cài SDK:

| File | Là gì |
|---|---|
| `bin/BhopAssist.exe` | Tool **bên ngoài**: nghe bàn phím bằng low-level hook, bắn input bằng `SendInput`. Không đọc/ghi bộ nhớ game, không inject DLL. |
| `bin/BhopTestbed.exe` | **Game của bạn**: mô phỏng vật lý di chuyển kiểu Source, để bạn có mục tiêu hợp pháp mà tune và nhìn thấy cửa sổ 1-tick. |

```powershell
.\build.ps1
.\bin\BhopAssist.exe --watch
.\bin\BhopAssist.exe --probe 60
.\bin\BhopAssist.exe --selftest
.\bin\BhopAssist.exe --profiles
.\bin\BhopTestbed.exe
```

---

## 1. "Làm ở ngoài" là tầng nào?

Xếp từ ít đụng chạm đến nhiều:

| Tầng | Cách làm | Game thấy gì |
|---|---|---|
| **0. Input tổng hợp** ← *cái này* | `SetWindowsHookEx` + `SendInput` | Y hệt một bàn phím thứ hai cắm thêm |
| 1. Driver input | Filter driver (Interception, Arduino/HID giả) | Không phân biệt được với phần cứng thật |
| 2. Đọc màn hình | Chụp frame buffer, nhận dạng ảnh | Không thấy gì cả |
| 3. Đọc bộ nhớ | `ReadProcessMemory` | Không thấy, nhưng anti-cheat thì thấy |
| 4. Inject | DLL injection, hook hàm game | Đây là can thiệp vào trong |

Tầng 0 đúng như bạn mô tả: **không hề can thiệp vào hệ thống của game.** Nó chỉ nói chuyện với Windows.

Điểm cần nhớ: `SendInput` gắn cờ `LLKHF_INJECTED` lên mỗi sự kiện. Low-level hook đọc được cờ đó (tool này dùng nó để không tự kích hoạt chính mình). Nhưng **Raw Input / DirectInput mà game đọc thì không có cờ này** — nên với game, input tổng hợp và input thật là một.

---

## 2. Vì sao bhop cần macro?

Trong vật lý kiểu Source (`src/Testbed.cs` cài đặt đúng thứ tự này):

```
tick:
  1. nhận ý định nhảy
  2. NẾU đang chạm đất VÀ có ý định nhảy  ->  nhảy
  3. NẾU đang chạm đất (và bước 2 không chạy) -> friction ăn mất tốc độ
  4. gia tốc:  dưới đất = tối đa 320 u/s | trên không = air-accel, trần 30 u/s
  5. trọng lực, di chuyển
```

Bước 2 chạy **trước** bước 3. Nhảy đúng cái tick vừa chạm đất → friction không bao giờ chạy → giữ nguyên tốc độ, rồi air-strafe cộng thêm. Trượt tick đó → friction ăn ngay.

Ở 64 tick, cửa sổ đó rộng **15.6 ms**. Giữ Space không ăn thua vì auto-repeat của Windows chỉ ~30 Hz và engine lọc bit repeat. Lăn tay ăn vì mỗi nấc lăn là một xung rời rạc — một cú lăn ra 5–8 xung. Macro thì đơn giản là phủ kín mọi tick.

Mở testbed, nhìn dải tick dưới cùng: cột **đỏ** = tick dính đất (mất tốc độ), **vàng** = nhảy. Bhop giỏi = không còn cột đỏ nào.

---

## 3. Hai kiểu game — và đây là lý do bản đầu không ăn ở ngoài testbed

Đây là phần quan trọng nhất của tài liệu này. **Không phải game nào cũng đọc bàn phím giống nhau.**

### LATCH — có hàng đợi sự kiện

GoldSrc (Half-Life, CS 1.6), Source (HL2, CS:S, CS:GO, CS2), và testbed đi kèm.

```
WM_KEYDOWN  ->  in_jump.state |= 1 (đang giữ) | 2 (vừa bấm)
WM_KEYUP    ->  in_jump.state &= ~1,  |= 4
CreateMove  ->  if (in_jump.state & 3)  cmd->buttons |= IN_JUMP
                in_jump.state &= 1
```

Bit 2 là **xung**, và nó sống cho tới lần `CreateMove` kế tiếp. Nên một cú bấm 4 ms nằm gọn giữa hai frame **vẫn được tính**. Bắn nhanh là ăn. Nhịp hợp lý: 150 Hz, giữ 4 ms.

### POLL — chụp ảnh trạng thái mỗi frame

Rất nhiều FPS F2P: DirectInput8 `GetDeviceState()`, hoặc Unity `Input.GetKey(...)`, gọi **một lần mỗi frame render**.

Đó không phải hàng đợi. Đó là ảnh chụp. **Phím xuống rồi lên trọn vẹn giữa hai lần chụp = không tồn tại.**

Ở 60 fps, hai lần chụp cách nhau 16.7 ms. Cú bấm 4 ms có xác suất lọt qua khe rất cao. Con số thật, đo bằng `--probe`:

```
> BhopAssist.exe --probe 60        (nhịp goldsrc: giữ 4.0 ms + nghỉ 2.7 ms)

 tool bắn ra          : 1499 cú nhấn  (149.9/giây)
 game LATCH sẽ nhận   : 1499 cú       -> tỷ lệ bắt 100.0 %
 game POLL 60 fps thấy:  254 cú       -> tỷ lệ bắt  16.9 %
```

83% cú nhấn bốc hơi. Và 254 cú còn lại cũng không đều — game nhìn thấy một cái phím **gần như đang bị giữ** với vài nhịp nhả ngẫu nhiên, đúng cái trạng thái mà mọi engine đều chặn không cho nhảy lại.

Đó là toàn bộ lý do "macro chạy mà nhân vật không nhảy".

### Cách sửa

Ở model POLL có đúng hai điều kiện, không có điều kiện thứ ba:

```
thời gian GIỮ   > 1 frame     (để ít nhất một lần chụp thấy phím XUỐNG)
thời gian NGHỈ  > 1 frame     (để ít nhất một lần chụp thấy phím LÊN, engine mới re-arm)
```

Ở 60 fps, nhân hệ số an toàn 1.35: giữ **22.5 ms**, nghỉ **22.5 ms** → 22 cú nhảy/giây.

```
> BhopAssist.exe --probe 60        (profile = poll60)

 tool bắn ra          : 222 cú nhấn  (22.2/giây)
 game POLL 60 fps thấy: 222 cú       -> tỷ lệ bắt 100.0 %
 KẾT LUẬN: 100.0 % cú nhấn đến được game, 22.2 cú nhảy/giây. Nhịp này đúng.
```

Chậm hơn 7 lần nhưng **đến nơi 100%** thay vì 17%.

> Nhịp POLL chạy tốt trên **cả hai** loại game — engine LATCH bắt hết mọi thứ, kể cả cú bấm dài 22 ms. Nên nếu không chắc game mình thuộc loại nào, cứ bắt đầu bằng `poll60`, rồi mới thử lên `goldsrc`/`source` cho nhanh hơn.

### `jitter_pct` giờ chỉ ăn vào khoảng NGHỈ

Bản đầu nhân jitter lên cả chu kỳ. Ở model POLL, một lần jitter âm kéo `hold` xuống dưới 1 frame là mất trắng cú nhảy đó. Bản này giữ **thời gian giữ là bất khả xâm phạm**, chỉ ngẫu nhiên hoá khoảng nghỉ, và tự kẹp jitter lại nếu nó định kéo khoảng nghỉ xuống dưới 1 frame.

---

## 4. Quy trình cho một game thật

**Bước 1 — tìm tên exe.**

```powershell
.\bin\BhopAssist.exe --watch
```

Alt-tab sang game. Nó in ra:

```
  process : SSJJ_BattleClient_Unity   (pid 11364)
  quyền   : KHÔNG đọc được -> nhiều khả năng chạy Administrator, cần --admin
  profile : không có sẵn -> tự chọn: profile = poll60 nếu game không phản ứng nhịp nhanh
  config  : target_process = SSJJ_BattleClient_Unity
```

**Bước 2 — điền vào `bin/config.ini`.**

```ini
target_process = SSJJ_BattleClient_Unity
profile        = poll60
game_fps       = 60
```

Với Half-Life / CS 1.6 thì khỏi cần chọn profile — để `profile = auto`, tool nhận ra `hl`, `cstrike`, `czero`, `hl2`, `csgo`, `cs2`, `bhoptestbed`... và tự đổi nhịp khi bạn alt-tab, in ra một dòng ngoài console.

**Bước 3 — kiểm tra trước khi vào game.**

```powershell
.\bin\BhopAssist.exe --probe 60
.\bin\BhopAssist.exe --selftest
```

`--selftest` cần dòng **thời gian giữ: min** không được thấp hơn mức đặt. Nếu min tụt xuống dưới, máy đang tải nặng và game kiểu POLL sẽ bỏ sót.

**Bước 4 — quyền.**

Nếu `--watch` báo "KHÔNG đọc được", game chạy ở mức toàn vẹn cao hơn tool. UIPI sẽ chặn `SendInput` **im lặng** — trả về 0, error 5, không có gì trên màn hình cả. Chạy:

```powershell
.\bin\BhopAssist.exe --admin
```

---

## 5. Phím kèm — SHIFT, CTRL, crouch-jump

`combo_key` bắn thêm một phím thứ hai cùng nhịp với phím nhảy. Ba chế độ, và **chọn sai chế độ thì tệ hơn là không bật**.

### Trước hết: SHIFT không làm nhảy cao hơn

Ở GoldSrc/Source (Half-Life, CS 1.6, CS:S), `SHIFT` mặc định là `+speed` = **đi bộ chậm**. Nó không nâng độ cao cú nhảy, mà còn ăn mất tốc độ bhop bạn vừa tích được. Vận tốc nhảy là hằng số của engine — `268.3216` u/s, chính là `JUMP_VEL` trong [Testbed.cs](src/Testbed.cs) — không phím nào đổi được nó.

Thứ cho bạn **lên cao hơn** ở dòng engine này là **crouch-jump**: nhảy trước, rồi co chân sau. Hull của nhân vật co lại và chân được kéo lên, nên bạn với được gờ cao hơn hẳn.

Ở game Unity kiểu SSJJ thì `SHIFT` thường là **sprint** → nhảy **xa hơn**, vẫn không cao hơn. Và sprint là *trạng thái*, không phải sự kiện.

Tool tự cảnh báo nếu bạn cấu hình lệch với profile đang chạy.

### `combo_mode = hold` — cho phím trạng thái

Bấm **một lần** lúc bật trợ lý, giữ suốt, nhả khi tắt. Đây là chế độ đúng cho SHIFT.

```ini
combo_key  = LSHIFT
combo_mode = hold
```

Lý do không dùng `with`: sprint là trạng thái. Nhấp nháy SHIFT 22 lần/giây làm game bật/tắt sprint liên tục — nhiều engine tính lại gia tốc mỗi lần chuyển, kết quả còn chậm hơn không giữ SHIFT.

### `combo_mode = after` — crouch-jump thật

```ini
combo_key       = LCTRL
combo_mode      = after
combo_offset_ms = 25
combo_hold_ms   = 0
```

Bấm CTRL **sau** SPACE 25 ms. Xem thứ tự thật bằng `--probe`:

```
> BhopAssist.exe --probe 60        (profile = poll60)

 MỘT NHỊP - đúng thứ tự tool sẽ bắn ra:
   +   0.0 ms   SPACE    xuống
   +  22.5 ms   SPACE    lên
   +  25.0 ms   LCTRL    xuống
   +  40.5 ms   LCTRL    lên
   +  45.0 ms   -- hết chu kỳ, nhịp sau bắt đầu --
```

Đừng dùng `with` cho CTRL: bấm CTRL cùng lúc với SPACE thì nhiều engine coi là **đang ngồi**, và chặn luôn cú nhảy.

### `combo_mode = with` — cùng xuống, cùng lên

Dùng khi game thật sự đòi hai phím cùng ở trạng thái xuống tại đúng thời điểm xử lý cú nhảy. Phím kèm luôn xuống **trước** phím nhảy một nhịp lệnh, để game thấy modifier đã sẵn sàng.

### Cả cụm phải lọt trong một chu kỳ

`combo_offset_ms + combo_hold_ms` không được vượt 90% chu kỳ, không thì nhịp sau đè lên và phím kèm **không bao giờ được nhả**. Tool tự kẹp lại và nói cho bạn biết đã kẹp xuống bao nhiêu:

```
[!] phím kèm đặt giữ 23.0 ms nhưng không lọt chu kỳ -> đã bị kẹp xuống 15.5 ms.
[!] phím kèm chỉ giữ 15.5 ms, không vượt 1 frame (16.7 ms) của game kiểu POLL
    -> game sẽ bỏ sót nó y hệt cú nhảy.
```

Vì **luật 1 frame ở mục 3 áp cho phím kèm y như phím nhảy**. Ở game POLL, muốn crouch-jump ăn thì cả SPACE lẫn CTRL đều phải giữ quá một frame, mà cả hai lại phải nằm gọn trong cùng một chu kỳ 45 ms.

Chỗ này chật nhưng lọt. Ở `poll60`, cấu hình vừa vặn là:

```ini
combo_offset_ms = 18
combo_hold_ms   = 19
```

```
   +   0.0 ms   SPACE    xuống
   +  18.0 ms   LCTRL    xuống
   +  22.5 ms   SPACE    lên
   +  37.0 ms   LCTRL    lên
   +  45.0 ms   -- hết chu kỳ --
```

Không bị kẹp, không cảnh báo. Còn `combo_offset_ms = 25` với `combo_hold_ms = 23` thì tràn — tool kẹp CTRL xuống 15.5 ms, tụt dưới một frame, và báo cho bạn đúng hai dòng ở trên. Chật quá thì hạ `poll_margin` xuống `1.15` để chu kỳ rộng ra.

---

## 6. Tool hoạt động ra sao

```
[hook thread]  SetWindowsHookEx(WH_KEYBOARD_LL)
               └─ thấy trigger_key xuống/lên  ->  bật/tắt cờ _active
                  (bỏ qua mọi input có cờ injected -> không tự lặp vô hạn)

[emit thread]  while (_active && đúng cửa sổ đang focus)
               └─ SendInput(keydown) -> chờ hold_ms -> SendInput(keyup)
                  -> chờ gap_ms (có jitter)
                  ↑ mỗi vòng đọc lại profile hiện hành, đổi game là đổi nhịp

[trigger poll] chỉ chạy khi hook bị từ chối: GetAsyncKeyState mỗi 1 ms
```

Vài chỗ dễ sai mà code này đã xử lý:

- **Giữ delegate hook ở field static.** Để biến cục bộ thì GC dọn mất, crash ngẫu nhiên sau vài phút.
- **Hook callback phải trả về nhanh.** Quá ~300 ms là Windows gỡ hook của bạn ra. Nên mọi việc nặng nằm ở thread khác.
- **`Thread.Sleep` sai ±15 ms.** Phải `timeBeginPeriod(1)` rồi Sleep(1) + spin-wait khi gần tới hạn. Self-test đo được sd = 0.13 ms ở nhịp 150 Hz.
- **Chốt focus.** Không có nó, tool spam Space vào Discord/Explorer. `target_process` nhận danh sách ngăn cách bằng dấu phẩy.
- **Không bắn vào chính mình.** Cửa sổ console của tool bị loại trừ theo pid.
- **Nhả phím khi tắt VÀ khi mất focus.** Không thì kẹt phím ở trạng thái đang giữ.

### Vì sao `trigger_source = poll` không dùng được khi trigger trùng emit

Đây là cái bẫy tự nhiên nhất khi hook bị chặn. Bạn nghĩ: bỏ hook, đọc `GetAsyncKeyState(SPACE)` là xong.

Không xong. Cú `KEYUP` mà chính tool bắn ra **xoá luôn trạng thái phím trong Win32k**. `GetAsyncKeyState` lập tức báo "đã nhả" dù người chơi vẫn đang giữ. Trigger tự tắt sau đúng một nhịp. Auto-repeat của bàn phím cũng không cứu được, vì Windows ngừng sinh repeat khi nó tưởng phím đã lên.

Hook thì không dính: nó thấy sự kiện **vật lý** và có cờ `injected` để phân biệt.

Nên: hook trùng phím thì được. Poll thì phải đổi `trigger_key` sang phím khác (`MOUSE4`, `MOUSE5`, `LSHIFT`...). Tool tự cảnh báo nếu bạn cấu hình sai cặp này.

### Khi `SendInput` không ăn

| Triệu chứng | Nguyên nhân | Xử lý |
|---|---|---|
| Trả về 0, error 5 | UIPI — game chạy Administrator, tool thì không | `BhopAssist.exe --admin` |
| Hook không nhận phím | Tool 32-bit không hook được process 64-bit | Đã build `/platform:x64` |
| Console đếm nhịp mà nhân vật đứng im | Game đọc kiểu POLL, nhịp đang là LATCH | `profile = poll60` / `poll100` / `poll144` |
| Vẫn đứng im | Game đọc scancode hay virtual-key? | Thử lần lượt `send_mode = scancode` → `vk` → `both` |
| Vẫn đứng im | Game dùng Raw Input ở chế độ exclusive | Phải lên tầng 1 (driver) — ngoài phạm vi tool này |

---

## 7. Nhưng nếu là game của bạn, đây mới là cách đúng

Bấm **F1** (jump buffer) hoặc **F2** (auto-hop) trong testbed. Chất lượng hop nhảy lên gần 100% ngay, không cần chạy tool ngoài nào cả.

Đó là điểm mấu chốt. Macro ngoài là thứ người ta phải viết khi **không sửa được game**. Bạn viết game thì bạn sửa được:

- **Jump buffer** — nhấn nhảy sớm vài tick vẫn được nhớ, tới lúc chạm đất thì kích hoạt. 4 tick là đủ. Đây là cách mọi platformer tử tế xử lý.
- **Coyote time** — vừa rời mép, vẫn cho nhảy thêm vài tick.
- **Auto-hop** — giữ nhảy thì tự hop. Nhiều game (Titanfall, Apex) chọn hẳn thế này.
- **Đọc input ở tầng sự kiện, không phải chụp trạng thái.** Bấm **F5** trong testbed để so LATCH ↔ POLL, **F7** để đổi tần số chụp. Ở POLL 60 fps, panel bên phải hiện thẳng ô **BỎ SÓT %** — đó là con số mà mọi game đọc `Input.GetKey` mỗi frame đang âm thầm chịu, kể cả với người chơi tay không.

Nếu bạn muốn tool ngoài vẫn là công dân hạng nhất, đừng bắt nó giả lập bàn phím — mở hẳn một cổng cho nó: named pipe hoặc websocket localhost, game nhận lệnh có cấu trúc. Rõ ràng hơn, debug được, và bạn kiểm soát được cái gì hợp lệ.

---

## 8. Tune

| Thông số | Ý nghĩa |
|---|---|
| `profile` | Chọn model + nhịp theo engine. `auto` = tự nhận theo tên process. Xem `--profiles`. |
| `game_fps` | Fps thật bạn đang chạy trong game. Chỉ dùng cho profile `poll*`, nhưng dùng đúng thì nhịp khớp hẳn. |
| `poll_margin` | Hệ số an toàn cho model POLL. `1.35` an toàn; `1.15` nhanh hơn nhưng dễ trượt khi game tụt fps. |
| `emit_hz` | Ép nhịp thủ công (model LATCH). `0` = theo profile. GoldSrc chạy `fps_max 250` thì đặt `250`. |
| `hold_ms` | Ép thời gian giữ. `0` = theo profile. Ở model POLL bị chặn không cho xuống dưới 1 frame. |
| `jitter_pct` | Lệch ngẫu nhiên ±%, **chỉ áp lên khoảng nghỉ**. `-1` = theo profile, `0` = đều tăm tắp. |
| `send_mode` | `scancode` (DirectInput/Raw Input) / `vk` (WM_KEYDOWN) / `both`. Thử lần lượt nếu game câm. |
| `combo_key` | Phím kèm bắn cùng nhịp. Chỉ nhận phím bàn phím. `NONE` = tắt. Xem mục 5. |
| `combo_mode` | `hold` (phím trạng thái: SHIFT/sprint) / `after` (crouch-jump) / `with`. |
| `combo_offset_ms` | Chỉ dùng cho `after`. Trễ so với phím nhảy. Bị kẹp nếu không lọt chu kỳ. |
| `combo_hold_ms` | Giữ phím kèm bao lâu. `0` = bằng phím nhảy. Ở game POLL cũng phải vượt 1 frame. |
| `trigger_source` | `auto` = hook, tự rơi về polling nếu hook bị từ chối. Xem cái bẫy ở mục 5. |
| `wheel_dir` | `up` / `down` / `both`. `both` = lăn xen kẽ, gấp đôi số xung — dùng khi game bind nhảy lên cả hai chiều. |
| `target_process` | Danh sách ngăn cách bằng dấu phẩy. Chốt an toàn. Để trống = bắn vào mọi cửa sổ. |

Quy trình tune trên testbed: mở testbed → **F5** sang POLL → **F7** chọn fps giống game thật → **F9** reset số liệu → chạy assist → giữ Space + strafe → nhìn ô **BỎ SÓT %** và **chất lượng hop**. Chỉnh `poll_margin` cho tới khi BỎ SÓT về 0, rồi hạ dần để lấy thêm số cú nhảy mỗi giây.
