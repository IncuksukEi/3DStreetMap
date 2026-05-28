**ĐỊNH NGHĨA CÁC BIẾN ĐẦU VÀO**

| **Biến đầu vào** | **Giải thích** |
| --- | --- |
| current\_speed | Tốc độ hiện tại của phương tiện |
| desired\_speed | Tốc độ mục tiêu mà phương tiện muốn đạt |
| max\_speed | Tốc độ tối đa cho phép |
| distance\_to\_front\_vehicle | Khoảng cách đến xe phía trước |
| relative\_speed\_front | Độ chênh lệch tốc độ với xe phía trước |
| traffic\_light\_state | Trạng thái đèn giao thông (đỏ/vàng/xanh) |
| distance\_to\_stop\_line | Khoảng cách đến vạch dừng |
| lane\_id | Làn đường hiện tại của xe |
| target\_lane\_id | Làn đường mục tiêu cần chuyển sang |
| vehicles\_in\_adjacent\_lane | Danh sách xe ở làn bên cạnh |
| intersection\_blocked | Giao lộ phía trước có bị kẹt hay không |
| vehicle\_in\_intersection | Có xe khác đang trong giao lộ hay không |

NGƯỠNG GỢI Ý:

* safe\_distance = 5–10m
* danger\_distance = 2–3m
* stop\_distance = 10–20m

MÔ TẢ CHI TIẾT CÁC BIẾN

**Nhóm tốc độ**

**current\_speed**

Tốc độ hiện tại của phương tiện (m/s hoặc km/h tùy hệ thống)
→ dùng để tính gia tốc, phanh và so sánh với các giới hạn

**desired\_speed**

Tốc độ mục tiêu mà phương tiện muốn đạt tới trong điều kiện bình thường
→ ví dụ: xe muốn chạy 60 km/h khi đường thoáng

**max\_speed**

Giới hạn tốc độ tối đa cho phép (do luật hoặc cấu hình xe)
→ đảm bảo xe không chạy quá nhanh

**safe\_distance**

Khoảng cách tối thiểu nên giữ với xe phía trước

**danger\_distance**

Khoảng cách nguy hiểm → cần phanh gấp

**Nhóm tương tác với xe phía trước**

**distance\_to\_front\_vehicle**

Khoảng cách từ đầu xe hiện tại đến đuôi xe phía trước (mét)
→ dùng để tránh va chạm và giữ khoảng cách

**relative\_speed\_front**

Chênh lệch tốc độ giữa xe hiện tại và xe phía trước

relative\_speed\_front = current\_speed - speed\_front\_vehicle

→ nếu giá trị dương: xe bạn đang tiến gần xe phía trước
→ nếu âm: xe phía trước đang đi nhanh hơn

**Nhóm đèn giao thông**

**traffic\_light\_state**

Trạng thái đèn giao thông phía trước

* RED: phải dừng
* YELLOW: chuẩn bị dừng
* GREEN: được phép đi

**distance\_to\_stop\_line**

Khoảng cách từ xe đến vạch dừng trước đèn giao thông

→ dùng để quyết định:

* khi nào bắt đầu phanh
* có kịp dừng không

**Nhóm làn đường**

**lane\_id**

ID của làn đường hiện tại xe đang đi

→ dùng để xác định vị trí xe trong hệ thống đường

**target\_lane\_id**

Làn đường mà xe cần chuyển sang (ví dụ để rẽ hoặc vượt)

→ nếu không có nhu cầu đổi làn thì có thể = lane\_id

**vehicles\_in\_adjacent\_lane**

Danh sách các xe trong làn bên cạnh (trái/phải)

→ mỗi phần tử nên có:

distance
speed

→ dùng để kiểm tra có an toàn khi đổi làn không

**Nhóm giao lộ**

**intersection\_blocked (true/false)**

Cho biết phía trước giao lộ có bị kẹt không

* true: không có chỗ trống → không nên đi vào
* false: có thể đi tiếp

**vehicle\_in\_intersection (true/false)**

Cho biết có xe khác đang nằm trong giao lộ hay không

→ dùng để:

* nhường đường
* tránh xung đột

**CÁC LUẬT CƠ BẢN**

**NHÓM 1: DI CHUYỂN AN TOÀN**

**B1 – Tránh va chạm phía trước**

**Mục tiêu:**
Ngăn phương tiện đâm vào xe phía trước

* Priority: 10
* Weight: 1.0
* Importance: Critical

**Input:**

distance\_to\_front\_vehicle
relative\_speed\_front

**Điều kiện:**

distance\_to\_front\_vehicle < safe\_distance

**Logic:**

if distance < danger\_distance:
 brake = 1.0
elif distance < safe\_distance:
 brake = (safe\_distance - distance) / safe\_distance
else:
 brake = 0

**Output:**

* brake ↑
* acceleration = 0

**Lưu ý:**

* Override toàn bộ luật khác
* Luôn được evaluate đầu tiên

**B2 – Tránh va chạm khi chuyển làn**

**Mục tiêu:**
Không đổi làn nếu làn bên cạnh không an toàn

* Priority: 10
* Weight: 1.0

**Input:**

distance\_front\_adjacent
distance\_back\_adjacent

**Điều kiện:**

đang có ý định đổi làn

**Logic:**

if distance\_front\_adjacent < safe\_distance
 or distance\_back\_adjacent < safe\_distance:
 lane\_change = 0

**Output:**

* Hủy lane change

**Lưu ý:**

* Áp dụng cho cả trái và phải

**B3 – Giữ khoảng cách an toàn**

**Mục tiêu:**
Duy trì khoảng cách ổn định

* Priority: 9
* Weight: 0.9

**Input:**

distance\_to\_front\_vehicle

**Logic:**

if distance < safe\_distance:
 brake = (safe\_distance - distance) / safe\_distance \* 0.7

**Output:**

* brake nhẹ

**Lưu ý:**

* Không override B1

**B4 – Không vượt quá tốc độ tối đa**

**Mục tiêu:**
Giữ xe trong giới hạn tốc độ

* Priority: 8
* Weight: 0.8

**Input:**

current\_speed
max\_speed

**Logic:**

if current\_speed > max\_speed:
 brake = (current\_speed - max\_speed) / max\_speed

**B5 – Dừng hoàn toàn khi quá gần**

**Mục tiêu:**
Ngăn va chạm khi khoảng cách cực nhỏ

* Priority: 10
* Weight: 1.0

**Input:**

distance\_to\_front\_vehicle

**Logic:**

if distance <= 0.5:
 speed = 0
 brake = 1.0

**NHÓM 2: ĐÈN GIAO THÔNG**

**B6 – Dừng khi đèn đỏ**

**Mục tiêu:**
Dừng trước vạch khi đèn đỏ

* Priority: 9
* Weight: 1.0

**Input:**

traffic\_light\_state
distance\_to\_stop\_line

**Logic:**

if RED and distance < stop\_distance:
 brake = (stop\_distance - distance) / stop\_distance

**Lưu ý:**

* Nếu đã qua vạch → bỏ qua

**B7 – Đi khi đèn xanh**

**Mục tiêu:**
Tiếp tục di chuyển khi đèn xanh

* Priority: 7
* Weight: 0.8

**Input:**

traffic\_light\_state
current\_speed
desired\_speed

**Logic:**

if GREEN:
 acceleration = (desired\_speed - current\_speed) / desired\_speed

**B8 – Chuẩn bị dừng khi đèn vàng**

**Mục tiêu:**
Giảm tốc an toàn

* Priority: 8
* Weight: 0.9

**Input:**

traffic\_light\_state
distance\_to\_stop\_line
current\_speed

**Logic:**

if YELLOW and distance < stop\_distance:
 brake = 0.5

**B9 – Không vượt đèn đỏ**

**Mục tiêu:**
Không cho xe chạy khi đèn đỏ

* Priority: 10
* Weight: 1.0

**Logic:**

if RED and distance\_to\_stop\_line > 0:
 acceleration = 0

**NHÓM 3: CHUYỂN ĐỘNG**

**B10 – Giữ tốc độ mong muốn**

**Mục tiêu:**
Ổn định tốc độ

* Priority: 5
* Weight: 0.6

**Logic:**

diff = desired\_speed - current\_speed
acceleration = diff / desired\_speed

**B11 – Tăng tốc mượt**

**Mục tiêu:**
Tránh tăng tốc đột ngột

* Priority: 4

**Logic:**

acceleration = min(acceleration, max\_acceleration)

**B12 – Giảm tốc mượt**

**Mục tiêu:**
Tránh phanh gấp

* Priority: 4

**Logic:**

brake = min(brake, max\_brake)

**B13 – Giới hạn gia tốc**

**Mục tiêu:**
Giữ chuyển động vật lý hợp lý

* Priority: 6

acceleration = clamp(acceleration, -a\_max, a\_max)

**B14 – Giữ hướng ổn định**

**Mục tiêu:**
Không đổi hướng liên tục

* Priority: 5

**Logic:**

if không có yêu cầu:
 lane\_change = 0

**NHÓM 4: LÀN ĐƯỜNG**

**B15 – Giữ làn hiện tại**

**Mục tiêu:**
Giảm dao động hành vi

* Priority: 6

default lane\_change = 0

**B16 – Đổi làn khi cần vượt**

**Mục tiêu:**
Vượt xe chậm

* Priority: 6

**Input:**

front\_speed
adjacent\_lane\_speed

**Logic:**

if front\_speed < current\_speed and lane trống:
 lane\_change = direction

**B17 – Đổi làn để chuẩn bị rẽ**

**Mục tiêu:**
Đúng làn trước giao lộ

* Priority: 7

**Logic:**

if distance\_to\_intersection < threshold:
 lane\_change = target\_lane

**B18 – Không đổi làn khi nguy hiểm**

**Mục tiêu:**
Bảo vệ an toàn

* Priority: 9

if xe gần:
 lane\_change = 0

**NHÓM 5: GIAO LỘ**

**B19 – Không vào giao lộ khi bị chặn**

**Mục tiêu:**
Tránh kẹt giao lộ

* Priority: 8

if intersection\_blocked:
 brake = 1.0

**B20 – Nhường xe trong giao lộ**

**Mục tiêu:**
Giữ luồng giao thông

* Priority: 7

if vehicle\_in\_intersection:
 brake = 0.5

**CƠ CHẾ TỔNG HỢP**

* if brake > 0: acceleration = 0
* final\_brake = max(all brake)
* final\_acceleration = sum(all acceleration)
* lane\_change ưu tiên theo B18 > B16 > B17
