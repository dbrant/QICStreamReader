# QICStreamReader

This is a series of C# projects for decoding and extracting backups from various ancient media formats,
made by legacy backup software that is no longer supported. Note that these tools are only for decoding
*images* of the backup media (i.e. binary dumps), and don't actually read from physical devices.

For many of these formats there doesn't seem to be any existing documentation, so I've had to reverse-engineer
them to the best of my ability.

The name QICStreamReader comes from the original format that I reverse-engineered, which was from the
QICStream software for MS-DOS, but there are now many more formats that are supported.

I'll emphasize that these are just my stream-of-consciousness code scraps, and may not work for decoding
your particular backup image. (If you have one that you can't decode, let me know!) Also, don't judge me by
the code quality throughout these scraps. Thanks!

## License

(applies to all files except `tapebackup_magic`, which is public domain.)

Copyright 2019+ Dmitry Brant

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

   http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
