# QICStreamReader
Decoder for ancient QICStream tape images.

In the very old days of tape backups, one of the tools that were used to record backups was QICStream,
which was a DOS utility that wrote the backup to a tape drive connected to the PC's floppy controller
or parallel port.

Unfortunately no documentation exists for the format of these backups, and no modern software
seems to support it natively any longer.
This is an attempt to reverse-engineer that format, which thankfully turns out to be
relatively simple. You can use this tool to extract the original files and directory
structure from a binary image that you took from an old QICStream tape.

## License

Copyright 2019 Dmitry Brant

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

   http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
