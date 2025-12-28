# Makefile for BlackBox Driver

# 如果是直接在 TX2 上編譯，使用當前運行的 Kernel 版本目錄
# 確保 TX2 上有 /lib/modules/$(shell uname -r)/build
# 如果沒有，可能需要安裝 kernel headers: sudo apt-get install nvidia-l4t-kernel-headers (視版本而定)

obj-m += blackbox_driver.o

KDIR := /lib/modules/$(shell uname -r)/build
PWD := $(shell pwd)

all:
	$(MAKE) -C $(KDIR) M=$(PWD) modules

clean:
	$(MAKE) -C $(KDIR) M=$(PWD) clean

# 方便測試用的載入與卸載指令
load:
	sudo insmod blackbox_driver.ko
	sudo chmod 666 /dev/blackbox

unload:
	sudo rmmod blackbox_driver