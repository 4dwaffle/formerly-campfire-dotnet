#include <vips/vips.h>
#include <stdio.h>
#include <string.h>
// image_processing filters unknown loader/saver options for the inferred
// codec, while explicit loader/saver calls remain strict.
static char *filtered_filename(const char *argument, int saving) {
  const char *open = strchr(argument, '[');
  if (!open || argument[strlen(argument) - 1] != ']') return g_strdup(argument);
  char *path = g_strndup(argument, open - argument);
  const char *operation = saving ? vips_foreign_find_save(path) : vips_foreign_find_load(path);
  if (!operation) { g_free(path); return g_strdup(argument); }
  VipsOperation *codec = vips_operation_new(operation);
  char *options = g_strndup(open + 1, strlen(open) - 2);
  char **entries = g_strsplit(options, ",", -1);
  GString *result = g_string_new(path);
  int first = 1;
  for (int i = 0; entries[i]; i++) {
    char **pair = g_strsplit(entries[i], "=", 2);
    if (g_object_class_find_property(G_OBJECT_GET_CLASS(codec), pair[0])) {
      g_string_append_c(result, first ? '[' : ',');
      g_string_append(result, entries[i]); first = 0;
    }
    g_strfreev(pair);
  }
  if (!first) g_string_append_c(result, ']');
  g_strfreev(entries); g_free(options); g_free(path); g_object_unref(codec);
  return g_string_free(result, FALSE);
}
int main(int argc, char **argv) {
  if (argc < 2 || vips_init(argv[0])) return 1;
  vips_block_untrusted_set(TRUE);
  vips_operation_block_set("VipsForeignLoadOpenslide", TRUE);
  int result;
  if (argc == 4 && strcmp(argv[1], "header") == 0) {
    VipsImage *image = vips_image_new_from_file(argv[3], NULL);
    char *value = NULL;
    result = !image || vips_image_get_as_string(image, argv[2], &value);
    if (value) { puts(value); g_free(value); }
    if (image) g_object_unref(image);
  } else if (argc == 4 && strcmp(argv[1], "addalpha") == 0) {
    VipsImage *image = vips_image_new_from_file(argv[2], NULL), *alpha = NULL;
    result = !image;
    if (!result) {
      if (vips_image_hasalpha(image)) alpha = g_object_ref(image);
      else result = vips_addalpha(image, &alpha, NULL);
      if (!result) result = vips_image_write_to_file(alpha, argv[3], NULL);
    }
    if (alpha) g_object_unref(alpha);
    if (image) g_object_unref(image);
  } else {
    VipsOperation *operation = vips_operation_new(argv[1]);
    result = !operation;
    if (operation) {
      GOptionContext *context = g_option_context_new(NULL);
      GOptionGroup *group = g_option_group_new("operation", "Operation", "Operation help", operation, NULL);
      g_option_context_add_group(context, group);
      vips_call_options(group, operation);
      GPtrArray *owned = g_ptr_array_new_with_free_func(g_free);
      char **arguments = g_new0(char *, argc);
      arguments[0] = g_strdup(argv[0]); g_ptr_array_add(owned, arguments[0]);
      for (int i = 2; i < argc; i++) {
        arguments[i - 1] = filtered_filename(argv[i], i == 3);
        g_ptr_array_add(owned, arguments[i - 1]);
      }
      int count = argc - 1;
      GError *error = NULL;
      if (!g_option_context_parse(context, &count, &arguments, &error)) {
        if (error) { fprintf(stderr, "%s\n", error->message); g_error_free(error); }
        result = 1;
      } else result = vips_call_argv(operation, count - 1, arguments + 1);
      g_option_context_free(context);
      vips_object_unref_outputs(VIPS_OBJECT(operation));
      g_object_unref(operation);
      g_free(arguments); g_ptr_array_free(owned, TRUE);
    }
  }
  if (result) fputs(vips_error_buffer(), stderr);
  vips_shutdown();
  return result ? 1 : 0;
}
