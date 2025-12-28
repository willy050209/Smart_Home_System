/**
 * @file blackbox_driver.c
 * @brief Linux Kernel Module for Smart Home Security Blackbox
 * @description
 * 實作一個字元驅動裝置 (Character Device)，功能包含：
 * 1. IOCTL: 接收 User Space 傳來的密碼驗證結果，並加上 Kernel 時間戳記寫入 Buffer。
 * 2. READ: 允許 User Space 讀取最近的 Log 紀錄 (Binary Struct 格式)。
 * * Target: NVIDIA Jetson TX2 (Linux Kernel 4.9 or 5.10+)
 */

#include <linux/module.h>
#include <linux/kernel.h>
#include <linux/init.h>
#include <linux/fs.h>
#include <linux/cdev.h>
#include <linux/uaccess.h>
#include <linux/slab.h>
#include <linux/timekeeping.h>
#include <linux/ioctl.h>
#include <linux/device.h>

#define DEVICE_NAME "blackbox"
#define CLASS_NAME "blackbox_class"
#define BUFFER_SIZE 100 // 儲存最近 100 筆紀錄

// --- 定義資料結構 (需與 C# 端一致) ---

// 定義 Log 結構體
typedef struct {
    char password[20];  // 輸入的密碼
    int result;         // 驗證結果 (1=成功, 0=失敗)
    long long timestamp;// Kernel 時間 (Ktime)
} log_entry_t;

// 定義 IOCTL 用於傳輸的資料結構 (不含 timestamp，因為由 Kernel 填寫)
typedef struct {
    char password[20];
    int result;
} auth_data_t;

// --- IOCTL Command 定義 ---
// Magic Number 'k', command 1. 
// _IOW 表示 User 寫入資料到 Kernel
#define IOCTL_WRITE_LOG _IOW('k', 1, auth_data_t)

// --- 全域變數 ---
static int major_number;
static struct class* blackbox_class  = NULL;
static struct device* blackbox_device = NULL;
static struct cdev blackbox_cdev;

// 環形緩衝區 (Circular Buffer)
static log_entry_t* log_buffer;
static int head = 0; // 寫入位置
//static int tail = 0; // 讀取位置 (目前此簡易實作主要用 head 覆蓋舊資料)
static int count = 0; // 目前儲存的總筆數 (max BUFFER_SIZE)

// --- 函數宣告 ---
static int     dev_open(struct inode *, struct file *);
static int     dev_release(struct inode *, struct file *);
static ssize_t dev_read(struct file *, char *, size_t, loff_t *);
static long    dev_ioctl(struct file *, unsigned int, unsigned long);

// File Operations 結構
static struct file_operations fops =
{
   .open = dev_open,
   .read = dev_read,
   .unlocked_ioctl = dev_ioctl, // 使用 unlocked_ioctl (新版 Kernel 標準)
   .release = dev_release,
};

// --- 模組初始化 ---
static int __init blackbox_init(void){
   printk(KERN_INFO "BlackBox: Initializing the BlackBox LKM\n");
   printk(KERN_INFO "BlackBox: IOCTL_WRITE_LOG value = 0x%08x\n", (unsigned int)IOCTL_WRITE_LOG);

   // 1. 動態配置 Major Number
   dev_t dev_num;
   int ret = alloc_chrdev_region(&dev_num, 0, 1, DEVICE_NAME);
   if (ret < 0) {
      printk(KERN_ALERT "BlackBox: Failed to allocate major number\n");
      return ret;
   }
   major_number = MAJOR(dev_num);
   printk(KERN_INFO "BlackBox: Registered correctly with major number %d\n", major_number);

   // 2. 註冊 Device Class (讓 /dev 下能自動產生節點)
   blackbox_class = class_create(THIS_MODULE, CLASS_NAME);
   if (IS_ERR(blackbox_class)){
      unregister_chrdev_region(dev_num, 1);
      printk(KERN_ALERT "BlackBox: Failed to register device class\n");
      return PTR_ERR(blackbox_class);
   }

   // 3. 註冊 Device Driver
   blackbox_device = device_create(blackbox_class, NULL, dev_num, NULL, DEVICE_NAME);
   if (IS_ERR(blackbox_device)){
      class_destroy(blackbox_class);
      unregister_chrdev_region(dev_num, 1);
      printk(KERN_ALERT "BlackBox: Failed to create the device\n");
      return PTR_ERR(blackbox_device);
   }

   // 4. 初始化 cdev 並加入系統
   cdev_init(&blackbox_cdev, &fops);
   ret = cdev_add(&blackbox_cdev, dev_num, 1);
   if (ret < 0){
       device_destroy(blackbox_class, dev_num);
       class_destroy(blackbox_class);
       unregister_chrdev_region(dev_num, 1);
       printk(KERN_ALERT "BlackBox: Failed to add cdev\n");
       return ret;
   }

   // 5. 分配 Buffer 記憶體
   log_buffer = kmalloc(sizeof(log_entry_t) * BUFFER_SIZE, GFP_KERNEL);
   if (!log_buffer) {
       printk(KERN_ALERT "BlackBox: Failed to allocate memory for log buffer\n");
       return -ENOMEM;
   }
   memset(log_buffer, 0, sizeof(log_entry_t) * BUFFER_SIZE);

   printk(KERN_INFO "BlackBox: Device class created correctly\n");
   return 0;
}

// --- 模組卸載 ---
static void __exit blackbox_exit(void){
   kfree(log_buffer);
   cdev_del(&blackbox_cdev);
   device_destroy(blackbox_class, MKDEV(major_number, 0));
   class_destroy(blackbox_class);
   unregister_chrdev_region(MKDEV(major_number, 0), 1);
   printk(KERN_INFO "BlackBox: Goodbye from the LKM!\n");
}

// --- Open ---
static int dev_open(struct inode *inodep, struct file *filep){
   printk(KERN_INFO "BlackBox: Device has been opened\n");
   return 0;
}

// --- Read: 讀取所有 Log ---
// 這裡將整個 Buffer 複製給 User Space
static ssize_t dev_read(struct file *filep, char *buffer, size_t len, loff_t *offset){
   int error_count = 0;
   size_t data_size = count * sizeof(log_entry_t);

   // 簡單實作：如果 offset > 0，表示已經讀過了，回傳 0 (EOF)
   if (*offset > 0) return 0;

   // 檢查 User 提供的 buffer 是否夠大
   if (len < data_size) {
       printk(KERN_ALERT "BlackBox: User buffer too small to read all logs\n");
       return -EFAULT; // 或者只回傳部分
   }

   // 複製資料到 User Space
   // 注意：這是一個簡單的 Dump，會將記憶體中的 struct array 直接複製出去
   // User Space 的 C# 程式需要以相同的 Struct 陣列來解析
   error_count = copy_to_user(buffer, log_buffer, data_size);

   if (error_count == 0){
      printk(KERN_INFO "BlackBox: Sent %zu bytes to the user\n", data_size);
      *offset += data_size; // 更新 offset
      return data_size;
   }
   else {
      printk(KERN_INFO "BlackBox: Failed to send %d characters to the user\n", error_count);
      return -EFAULT;
   }
}

// --- IOCTL: 寫入 Log ---
static long dev_ioctl(struct file *file, unsigned int cmd, unsigned long arg) {
    auth_data_t user_data;
    struct timespec64 ts;

    switch(cmd) {
        case IOCTL_WRITE_LOG:
            // 1. 從 User Space 讀取資料
            if(copy_from_user(&user_data, (auth_data_t*)arg, sizeof(auth_data_t))) {
                return -EFAULT;
            }

            // 2. 取得目前 Kernel 時間
            ktime_get_real_ts64(&ts);

            // 3. 寫入環形緩衝區
            strncpy(log_buffer[head].password, user_data.password, 20);
            log_buffer[head].password[19] = '\0'; // 確保字串結尾
            log_buffer[head].result = user_data.result;
            log_buffer[head].timestamp = (long long)ts.tv_sec; // 只存秒數，簡化處理

            printk(KERN_INFO "BlackBox: Logged - Pass:%s, Res:%d at %lld\n", 
                    log_buffer[head].password, log_buffer[head].result, log_buffer[head].timestamp);

            // 4. 更新指標
            head = (head + 1) % BUFFER_SIZE;
            if (count < BUFFER_SIZE) count++;
            
            break;
        default:
            return -EINVAL;
    }
    return 0;
}

// --- Release ---
static int dev_release(struct inode *inodep, struct file *filep){
   printk(KERN_INFO "BlackBox: Device successfully closed\n");
   return 0;
}

module_init(blackbox_init);
module_exit(blackbox_exit);

MODULE_LICENSE("GPL");
MODULE_AUTHOR("SmartHome Student");
MODULE_DESCRIPTION("A simple BlackBox driver for Smart Home System");
MODULE_VERSION("1.0");