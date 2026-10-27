#region "copyright"

/*
    Copyright © 2025 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

namespace System.IO {
    /// <summary>
    /// Stand-in for WindowsBase's System.IO.FileFormatException, which WPF throws for
    /// streams or files that don't match the expected format.
    /// </summary>
    public class FileFormatException : FormatException {
        public FileFormatException() : base() { }

        public FileFormatException(string message) : base(message) { }

        public FileFormatException(string message, Exception innerException)
            : base(message, innerException) { }

        public FileFormatException(Uri sourceUri) : base() {
            SourceUri = sourceUri;
        }

        public FileFormatException(Uri sourceUri, string message) : base(message) {
            SourceUri = sourceUri;
        }

        public FileFormatException(Uri sourceUri, Exception innerException) : base(null, innerException) {
            SourceUri = sourceUri;
        }

        public FileFormatException(Uri sourceUri, string message, Exception innerException)
            : base(message, innerException) {
            SourceUri = sourceUri;
        }

        public Uri SourceUri { get; }
    }
}
