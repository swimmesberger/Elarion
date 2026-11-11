# Third-party notices

Elarion is licensed under the Apache License 2.0 (see [LICENSE](LICENSE)). It incorporates the third-party
material listed below, which remains under its original license.

## dotnet/aspnetcore — Microsoft.Extensions.Validation walker

- **Used in:** `src/Elarion.Validation/Metadata/` (the `net11.0` build of the `Elarion.Validation` package).
- **Source:** the validation walker templates shipped with the `Microsoft.Extensions.Validation` 11.0.0 source
  generator, from <https://github.com/dotnet/aspnetcore> (`src/Validation`). Adapted to Elarion's code style; see
  the header of each file for its differences from the template.

```text
The MIT License (MIT)

Copyright (c) .NET Foundation and Contributors

All rights reserved.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
