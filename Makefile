CC ?= gcc
AR ?= ar
DOTNET ?= dotnet

CPPFLAGS ?=
CPPFLAGS += -Iinclude -Igenerated
CFLAGS ?= -O2
CFLAGS += -std=c11 -Wall -Wextra -Wpedantic
SHARED_CFLAGS ?= -fPIC

BUILD_DIR := build
LIB_DIR := lib
ADAPTER_OBJ := $(BUILD_DIR)/fsharp_polycall.o
STATIC_LIB := $(LIB_DIR)/libfsharp_polycall.a
TEST_BIN := $(BUILD_DIR)/fsharp_polycall_adapter_test

ifeq ($(OS),Windows_NT)
EXE_EXT := .exe
NATIVE_LIB := $(LIB_DIR)/fsharp_polycall.dll
MOCK_NATIVE_LIB := $(BUILD_DIR)/fsharp_polycall.dll
TEST_BIN := $(TEST_BIN)$(EXE_EXT)
else
UNAME_S := $(shell uname -s)
ifeq ($(UNAME_S),Darwin)
NATIVE_LIB := $(LIB_DIR)/libfsharp_polycall.dylib
MOCK_NATIVE_LIB := $(BUILD_DIR)/libfsharp_polycall.dylib
else
NATIVE_LIB := $(LIB_DIR)/libfsharp_polycall.so
MOCK_NATIVE_LIB := $(BUILD_DIR)/libfsharp_polycall.so
endif
endif

.DEFAULT_GOAL := all

.PHONY: all
all: $(STATIC_LIB)

$(BUILD_DIR) $(LIB_DIR):
ifeq ($(OS),Windows_NT)
	@if not exist "$@" mkdir "$@"
else
	@mkdir -p $@
endif

$(ADAPTER_OBJ): c_src/fsharp_polycall.c include/fsharp_polycall.h generated/polycall/polycall_ffi.h | $(BUILD_DIR)
	$(CC) $(CPPFLAGS) $(CFLAGS) -MMD -MP -c $< -o $@

$(STATIC_LIB): $(ADAPTER_OBJ) | $(LIB_DIR)
	$(AR) rcs $@ $^

$(TEST_BIN): c_src/fsharp_polycall.c tests/polycall_ffi_mock.c tests/fsharp_polycall_adapter_test.c | $(BUILD_DIR)
	$(CC) $(CPPFLAGS) -Itests $(CFLAGS) $^ -o $@

.PHONY: test
test: $(TEST_BIN)
	$(TEST_BIN)

.PHONY: native
native: | $(LIB_DIR)
ifeq ($(OS),Windows_NT)
	@if "$(strip $(POLYCALL_LDFLAGS))"=="" (echo Set POLYCALL_LDFLAGS to the libpolycall v1.5 linker flags & exit /b 2)
else
	@test -n "$(POLYCALL_LDFLAGS)" || (echo "Set POLYCALL_LDFLAGS to the libpolycall v1.5 linker flags" && exit 2)
endif
	$(CC) $(CPPFLAGS) $(CFLAGS) $(SHARED_CFLAGS) -DFSHARP_POLYCALL_BUILD_SHARED \
		-shared c_src/fsharp_polycall.c $(POLYCALL_LDFLAGS) -o $(NATIVE_LIB)

$(MOCK_NATIVE_LIB): c_src/fsharp_polycall.c tests/polycall_ffi_mock.c | $(BUILD_DIR)
	$(CC) $(CPPFLAGS) -Itests $(CFLAGS) $(SHARED_CFLAGS) \
		-DFSHARP_POLYCALL_BUILD_SHARED -shared $^ -o $@

.PHONY: dotnet-build
dotnet-build:
	$(DOTNET) build src/FSharpPolycall/FSharpPolycall.fsproj --configuration Release

.PHONY: test-fsharp
test-fsharp: $(MOCK_NATIVE_LIB)
ifeq ($(OS),Windows_NT)
	@set "PATH=$(abspath $(BUILD_DIR));%PATH%" && $(DOTNET) run \
		--project tests/FSharpPolycall.Smoke/FSharpPolycall.Smoke.fsproj \
		--configuration Release
else ifeq ($(UNAME_S),Darwin)
	DYLD_LIBRARY_PATH="$(abspath $(BUILD_DIR)):$$DYLD_LIBRARY_PATH" $(DOTNET) run \
		--project tests/FSharpPolycall.Smoke/FSharpPolycall.Smoke.fsproj \
		--configuration Release
else
	LD_LIBRARY_PATH="$(abspath $(BUILD_DIR)):$$LD_LIBRARY_PATH" $(DOTNET) run \
		--project tests/FSharpPolycall.Smoke/FSharpPolycall.Smoke.fsproj \
		--configuration Release
endif

.PHONY: verify-dry
verify-dry:
ifeq ($(OS),Windows_NT)
	powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify-dry.ps1
else
	sh scripts/verify-dry.sh
endif

.PHONY: clean
clean:
ifeq ($(OS),Windows_NT)
	@if exist "$(BUILD_DIR)" rmdir /s /q "$(BUILD_DIR)"
	@if exist "$(LIB_DIR)" rmdir /s /q "$(LIB_DIR)"
	@if exist "src\FSharpPolycall\bin" rmdir /s /q "src\FSharpPolycall\bin"
	@if exist "src\FSharpPolycall\obj" rmdir /s /q "src\FSharpPolycall\obj"
	@if exist "tests\FSharpPolycall.Smoke\bin" rmdir /s /q "tests\FSharpPolycall.Smoke\bin"
	@if exist "tests\FSharpPolycall.Smoke\obj" rmdir /s /q "tests\FSharpPolycall.Smoke\obj"
else
	rm -rf $(BUILD_DIR) $(LIB_DIR) \
		src/FSharpPolycall/bin src/FSharpPolycall/obj \
		tests/FSharpPolycall.Smoke/bin tests/FSharpPolycall.Smoke/obj
endif

-include $(ADAPTER_OBJ:.o=.d)
