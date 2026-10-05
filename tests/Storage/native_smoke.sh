#!/bin/sh
set -eu
cd /check
vips black source.png 200 100 --bands 3
./vips-safe thumbnail source.png resized.tiff 80 --height 40 --size down
test "$(./vips-safe header width resized.tiff)" = 80
test "$(./vips-safe header height resized.tiff)" = 40
printf '3 3 24 0\n-1 -1 -1\n-1 32 -1\n-1 -1 -1\n' > sharpen.mat
./vips-safe conv resized.tiff sharpened.tiff sharpen.mat --precision integer
./vips-safe copy sharpened.tiff 'result.png[Q=80]'
test "$(./vips-safe header width result.png)" = 80
test "$(./vips-safe header height result.png)" = 40
test "$(od -An -tx1 -N8 result.png | tr -d ' \n')" = 89504e470d0a1a0a
./vips-safe rot result.png rotated.png d90
test "$(./vips-safe header width rotated.png)" = 40
test "$(./vips-safe header height rotated.png)" = 80
./vips-safe addalpha result.png alpha.png
test "$(./vips-safe header bands alpha.png)" = 4
vips copy source.png untrusted.bmp
if ./vips-safe header width untrusted.bmp > blocked.stdout 2> blocked.stderr; then
  echo 'FAIL untrusted BMP loader was allowed'
  exit 1
fi
test -s blocked.stderr
echo 'PASS native libvips: option parsing, real PNG80x40, mild convolution, rotate40x80, alpha, inferred saver filtering and blocked BMP loader'
