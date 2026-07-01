cmake_minimum_required(VERSION 3.24)

project(CuttingKernel LANGUAGES CXX)

if(POLICY CMP0167)
    cmake_policy(SET CMP0167 OLD)
endif()

option(CUTTING_KERNEL_USE_OPENVDB "Build the cutting kernel with OpenVDB support" ON)

add_library(cutting_kernel_v43 SHARED
    src/cutting_kernel.cpp
)

target_compile_features(cutting_kernel_v43 PRIVATE cxx_std_17)
target_compile_options(cutting_kernel_v43 PRIVATE
    -fvisibility=hidden
)
target_include_directories(cutting_kernel_v43 PRIVATE
    include
)

set_target_properties(cutting_kernel_v43 PROPERTIES
    OUTPUT_NAME "cutting_kernel_v43"
    PREFIX ""
)

if(APPLE)
    set_target_properties(cutting_kernel_v43 PROPERTIES
        MACOSX_RPATH ON
        INSTALL_RPATH "/opt/homebrew/lib;/opt/homebrew/opt/openvdb/lib"
        BUILD_WITH_INSTALL_RPATH ON
    )
endif()

if(CUTTING_KERNEL_USE_OPENVDB)
    list(APPEND CMAKE_MODULE_PATH "/opt/homebrew/opt/openvdb/lib/cmake/OpenVDB")
    find_package(OpenVDB REQUIRED)

    target_compile_definitions(cutting_kernel_v43 PRIVATE SDF_USE_OPENVDB=1)
    target_link_libraries(cutting_kernel_v43 PRIVATE OpenVDB::openvdb)
endif()
