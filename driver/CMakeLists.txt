# Сборка драйвера RollWorld (нужна только если хочешь пересобрать; готовая DLL уже лежит в package/).
#   cmake -S . -B build -A x64
#   cmake --build build --config Release
cmake_minimum_required(VERSION 3.15)
project(rollworld_driver LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

add_library(driver_01rollworld SHARED
    src/driver_main.cpp
    third_party/minhook/src/buffer.c
    third_party/minhook/src/hook.c
    third_party/minhook/src/trampoline.c
    third_party/minhook/src/hde/hde64.c)

target_include_directories(driver_01rollworld PRIVATE src third_party/openvr third_party/minhook/include)
set_target_properties(driver_01rollworld PROPERTIES PREFIX "")

if(MSVC)
    target_compile_options(driver_01rollworld PRIVATE /W3 /MP)
    # статическая CRT: на компьютере не нужен пакет Visual C++ Redistributable
    set_property(TARGET driver_01rollworld PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded")
else()
    target_link_options(driver_01rollworld PRIVATE -static -static-libgcc -static-libstdc++ -s)
endif()

# готовая DLL сразу кладётся в пакет драйвера
set(PKG_BIN "${CMAKE_CURRENT_SOURCE_DIR}/package/01rollworld/bin/win64")
add_custom_command(TARGET driver_01rollworld POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E make_directory "${PKG_BIN}"
    COMMAND ${CMAKE_COMMAND} -E copy $<TARGET_FILE:driver_01rollworld> "${PKG_BIN}/")

# тест без SteamVR (подделка vrserver): cmake --build build --target mock_host
add_executable(mock_host EXCLUDE_FROM_ALL test/mock_host.cpp)
target_include_directories(mock_host PRIVATE src third_party/openvr)
if(NOT MSVC)
    target_link_options(mock_host PRIVATE -static -static-libgcc -static-libstdc++)
endif()
add_dependencies(mock_host driver_01rollworld)
add_custom_command(TARGET mock_host POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy $<TARGET_FILE:driver_01rollworld> $<TARGET_FILE_DIR:mock_host>)
