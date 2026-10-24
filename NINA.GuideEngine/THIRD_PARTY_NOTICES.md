# Third-party notices

## PHD2

Parts of `NINA.GuideEngine` (the internal guider engine) are C# ports of PHD2 (https://github.com/OpenPHDGuiding/phd2,
reference commit `a6c027227c3dcbc2978185c8abf3979ac1d39d32`). Every ported file carries
`SPDX-License-Identifier: MPL-2.0 AND BSD-3-Clause`, the original PHD2 copyright lines and the name
of the PHD2 source file(s) it was ported from. PHD2 is distributed under the following license:

```
Redistribution and use in source and binary forms, with or without modification, are permitted
provided that the following conditions are met:
  Redistributions of source code must retain the above copyright notice, this list of conditions
   and the following disclaimer.
  Redistributions in binary form must reproduce the above copyright notice, this list of
   conditions and the following disclaimer in the documentation and/or other materials provided
   with the distribution.
  Neither the name of Craig Stark, Stark Labs, Bret McKee, Dad Dog Development, the Open PHD
   Guiding project nor the names of its contributors may be used to endorse or promote products
   derived from this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR
IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND
FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR
CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER
IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT
OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

## KStars

KStars (GPL-2.0-or-later) was studied for ideas only (multi-star correspondence fallback, star
selection scoring). No KStars code is included.
