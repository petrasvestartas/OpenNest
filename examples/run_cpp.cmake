# Runs the built C++ example from whichever layout the generator produced (single-config: <bin>/;
# multi-config / MSVC: <bin>/Release/). Invoked by the `run_examples` target. CPP_BIN is passed in.
file(GLOB _exe
  "${CPP_BIN}/nest_demo"
  "${CPP_BIN}/nest_demo.exe"
  "${CPP_BIN}/Release/nest_demo"
  "${CPP_BIN}/Release/nest_demo.exe")

if(NOT _exe)
  message(FATAL_ERROR "nest_demo executable not found under ${CPP_BIN}")
endif()

list(GET _exe 0 _e)
get_filename_component(_dir "${_e}" DIRECTORY)   # run from the exe's dir so it finds the engine libs next to it
execute_process(COMMAND "${_e}" WORKING_DIRECTORY "${_dir}" RESULT_VARIABLE _rc)
if(NOT _rc EQUAL 0)
  message(FATAL_ERROR "nest_demo exited with code ${_rc}")
endif()
