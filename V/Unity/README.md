# Knife Follow-Up — V chạy đúng lúc nhát chém nặng kết thúc (làm trong game)

Thay cho `VAssist.exe` (tool ngoài) khi game có anti-cheat như XIGNCODE, hoặc khi bạn có source game.

| | VAssist (tool ngoài) | Module này (trong game) |
|---|---|---|
| Biết lúc chém xong | **Đoán** bằng `delay_ms` | **Biết chính xác**: game tự báo |
| Sai số | ±1 frame, phải cộng biên an toàn | 0: chạy đúng frame gỡ khoá |
| Mỗi loại dao | Tune lại `delay_ms` | Tự đúng |
| Anti-cheat | Bị XIGNCODE chặn | Không có input giả nào để chặn |
| Bấm V tay giữa lúc chém | Bị nuốt | Được nhớ, chém xong chạy |

## File

```
KnifeFollowUp/
  FollowUpGate.cs          logic thuần, không phụ thuộc Unity
  HeavySlashFollowUp.cs    MonoBehaviour gắn lên nhân vật local
  HeavySlashStateHook.cs   StateMachineBehaviour gắn vào state chém nặng (tuỳ chọn)
  Editor/
    FollowUpGateSelfTest.cs   13 kịch bản kiểm tra
    KnifeFollowUpMenu.cs      menu Tools > Knife Follow-Up > Chay self-test
```

Chép cả thư mục `KnifeFollowUp/` vào `Assets/` của project. Code viết theo cú pháp C# 5, nên biên dịch được trên mọi bản Unity.

## Cách hoạt động

```
không đang chém      bấm V          -> V chạy ngay (không khác hiện tại)
đang chém            bấm V          -> nhớ lại, chém xong mới chạy (không bao giờ bị nuốt)
autoFollowUp = true  chém xong      -> tự chạy V, không cần bấm
bị ngắt giữa chừng   CancelHeavySlash -> bỏ V đang nhớ
```

---

## Gắn vào game: 3 chỗ

Thêm component **`HeavySlashFollowUp`** vào nhân vật của **người chơi local**, rồi:

### 1. Phím V đi qua component

```csharp
// trước:
if (Input.GetKeyDown(KeyCode.V)) weapon.DoV();

// sau:
if (Input.GetKeyDown(KeyCode.V)) followUp.PressV();
```

Nối hành động V thật vào component, chọn **một** trong hai cách:
- Kéo thả vào `On Follow Up` trong Inspector.
- Hoặc nối bằng code:

  ```csharp
  followUp.FollowUp += reason => weapon.DoV();
  ```

Hành động phải là **đúng hàm mà phím V thật đang gọi**. Như vậy đường mạng, kiểm tra phía server và anti-cheat đều không đổi gì.

### 2. Báo nhát chém bắt đầu và kết thúc — chọn MỘT trong ba cách

**a) Trong code vũ khí — chính xác nhất.** Gọi ở đúng chỗ game khoá và gỡ khoá hành động:

```csharp
void StartHeavySlash()
{
    // ... code chém nặng hiện có ...
    followUp.BeginHeavySlash();                  // hoặc BeginHeavySlash(knife.heavyLockSeconds)
}

void OnHeavySlashLockReleased()                  // chỗ game cho phép hành động lại
{
    // ... code gỡ khoá hiện có ...
    followUp.EndHeavySlash();                    // gọi SAU khi gỡ khoá, V chạy ngay tại đây
}
```

Nếu game khoá bằng thời gian lấy từ data của từng loại dao, chỉ cần `BeginHeavySlash(lockSeconds)`. Component tự kết thúc đúng lúc, không cần gọi `EndHeavySlash`.

**b) Trong Animator — không sửa code.** Mở Animator Controller, chọn state chém nặng → **Add Behaviour** → `HeavySlashStateHook`:
- `End At Normalized Time`: lúc gỡ khoá trong clip (0..1). Nên đặt bằng Exit Time của transition đi ra khỏi state.
- `Exit Counts As End`: rời state sớm hơn mốc đó thì coi như chém xong (mặc định) hay coi như bị ngắt.

Không dùng được với transition từ state chém về chính nó (self-transition). Nếu chém liên tiếp thì tách thành hai state.

**c) Animation Event.** Trong clip chém nặng, thêm event `AnimEvent_HeavySlashBegin` ở đầu clip và `AnimEvent_HeavySlashEnd` ở frame gỡ khoá. Component phải nằm **cùng GameObject** với Animator.

### 3. Chỗ bị ngắt

```csharp
void OnWeaponSwitched() { followUp.CancelHeavySlash(); /* ... */ }
void OnHitStun()        { followUp.CancelHeavySlash(); /* ... */ }
void OnDeath()          { followUp.CancelHeavySlash(); /* ... */ }
```

Thiếu bước này thì V đang nhớ vẫn chạy sau khi đã đổi sang súng.

---

## Tuỳ chọn (Inspector)

| Tuỳ chọn | Ý nghĩa |
|---|---|
| `Auto Follow Up` | Chém xong tự chạy V. Đổi lúc runtime được qua thuộc tính `followUp.AutoFollowUp = true`, ví dụ từ menu setting. |
| `Buffer Seconds` | Chỉ nhớ V nếu bấm trong N giây cuối trước khi chém xong. `0` = nhớ suốt nhát chém. |
| `Defer To Next Frame` | Bật nếu V bị từ chối vì game gỡ khoá **sau** `EndHeavySlash()` trong cùng frame (thứ tự Animation Event). V chạy ở frame kế tiếp. |
| `Use Unscaled Time` | Game có slow-motion hoặc pause bằng `timeScale`. |
| `Log Timing` | In thời gian thật của mỗi nhát chém ra Console, để biết từng loại dao chém bao lâu. |

## Lưu ý multiplayer

- **Chỉ gắn component cho nhân vật local.** Nhân vật người khác cũng chơi animation chém. `HeavySlashStateHook` tự bỏ qua nhân vật không có component, nhưng nếu gắn component cho họ thì `Auto Follow Up` sẽ chạy V thay họ trên máy bạn.
- Module chỉ quyết định **khi nào** gọi hành động V. Xác thực nằm ở hàm V hiện có, vẫn chạy như bấm tay.

## Kiểm tra

- **Logic:** `Tools > Knife Follow-Up > Chay self-test`. Console phải in `13 dung, 0 sai`.
- **Trong game:** bật `Log Timing`, chém nặng, rồi bấm V ngay giữa lúc chém. V phải chạy đúng lúc chém xong, không sớm, không bị nuốt.
- Chạy thử trên bản build nội bộ, không phải bản đang bật XIGNCODE.
