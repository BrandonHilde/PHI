/* Written by vendor/bindgen.py, and built into the library by vendor/fetch.py: a new one of
   each struct PHI holds only by its address (its size is miniaudio's own business, and
   differs between systems), all zeros, and what frees one. */
#include <stdlib.h>
#include "miniaudio.h"
void *phi_new_ma_event(void) { return calloc(1, sizeof(ma_event)); }
void *phi_new_ma_semaphore(void) { return calloc(1, sizeof(ma_semaphore)); }
void *phi_new_ma_log(void) { return calloc(1, sizeof(ma_log)); }
void *phi_new_ma_resampler(void) { return calloc(1, sizeof(ma_resampler)); }
void *phi_new_ma_data_converter(void) { return calloc(1, sizeof(ma_data_converter)); }
void *phi_new_ma_fence(void) { return calloc(1, sizeof(ma_fence)); }
void *phi_new_ma_async_notification_event(void) { return calloc(1, sizeof(ma_async_notification_event)); }
void *phi_new_ma_job(void) { return calloc(1, sizeof(struct ma_job)); }
void *phi_new_ma_job_queue(void) { return calloc(1, sizeof(ma_job_queue)); }
void *phi_new_ma_device_job_thread(void) { return calloc(1, sizeof(ma_device_job_thread)); }
void *phi_new_ma_device_id(void) { return calloc(1, sizeof(ma_device_id)); }
void *phi_new_ma_device_info(void) { return calloc(1, sizeof(ma_device_info)); }
void *phi_new_ma_context(void) { return calloc(1, sizeof(struct ma_context)); }
void *phi_new_ma_device(void) { return calloc(1, sizeof(struct ma_device)); }
void *phi_new_ma_decoder(void) { return calloc(1, sizeof(struct ma_decoder)); }
void *phi_new_ma_resource_manager_data_buffer(void) { return calloc(1, sizeof(struct ma_resource_manager_data_buffer)); }
void *phi_new_ma_resource_manager_data_stream(void) { return calloc(1, sizeof(struct ma_resource_manager_data_stream)); }
void *phi_new_ma_resource_manager_data_source(void) { return calloc(1, sizeof(struct ma_resource_manager_data_source)); }
void *phi_new_ma_resource_manager(void) { return calloc(1, sizeof(struct ma_resource_manager)); }
void *phi_new_ma_engine_node(void) { return calloc(1, sizeof(ma_engine_node)); }
void *phi_new_ma_sound(void) { return calloc(1, sizeof(struct ma_sound)); }
void *phi_new_ma_sound_inlined(void) { return calloc(1, sizeof(struct ma_sound_inlined)); }
void phi_delete(void *made) { free(made); }
