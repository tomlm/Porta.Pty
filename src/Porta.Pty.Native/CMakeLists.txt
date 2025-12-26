cmake_minimum_required(VERSION 3.10)
project(porta_pty C)

set(CMAKE_C_STANDARD 99)
set(CMAKE_C_STANDARD_REQUIRED ON)

# Set default build type to Release
if(NOT CMAKE_BUILD_TYPE)
    set(CMAKE_BUILD_TYPE Release)
endif()

# Compiler flags
set(CMAKE_C_FLAGS "${CMAKE_C_FLAGS} -Wall -Wextra")
set(CMAKE_C_FLAGS_RELEASE "${CMAKE_C_FLAGS_RELEASE} -O2")

# Hide symbols by default, only export what we mark with PTY_EXPORT
set(CMAKE_C_VISIBILITY_PRESET hidden)

# Create shared library
add_library(porta_pty SHARED porta_pty.c)

# Platform-specific settings
if(APPLE)
    # macOS: link against system libraries
    target_link_libraries(porta_pty PRIVATE "-framework CoreFoundation")
    
    # Set install name for macOS
    set_target_properties(porta_pty PROPERTIES
        INSTALL_RPATH "@loader_path"
        BUILD_WITH_INSTALL_RPATH TRUE
        MACOSX_RPATH TRUE
    )
    
    # Output as .dylib
    set_target_properties(porta_pty PROPERTIES
        SUFFIX ".dylib"
        PREFIX "lib"
    )
else()
    # Linux: link against libutil for forkpty
    target_link_libraries(porta_pty PRIVATE util)
    
    # Output as .so
    set_target_properties(porta_pty PROPERTIES
        SUFFIX ".so"
        PREFIX "lib"
    )
endif()

# Set output directory
set_target_properties(porta_pty PROPERTIES
    LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
)

# Install rules
install(TARGETS porta_pty
    LIBRARY DESTINATION lib
    RUNTIME DESTINATION bin
)
