include_guard()
get_filename_component(_polar_root "${CMAKE_CURRENT_LIST_DIR}/../../.." ABSOLUTE)
if(NOT TARGET Polar::SDK)
  add_library(Polar::SDK SHARED IMPORTED)
  set_target_properties(Polar::SDK PROPERTIES
    IMPORTED_LOCATION "${_polar_root}/bin/polar_sdk.dll"
    IMPORTED_IMPLIB "${_polar_root}/lib/polar_sdk.lib"
    INTERFACE_INCLUDE_DIRECTORIES "${_polar_root}/include")
endif()
unset(_polar_root)
