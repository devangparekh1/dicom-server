﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

namespace Microsoft.Health.Dicom.Core.Features.Store
{
    /// <summary>
    /// If any of the failure codes are modified, please check they match the DICOM conformance statement.
    /// </summary>
    internal static class FailureReasonCodes
    {
        /// <summary>
        /// General exception in processing the DICOM instance.
        /// </summary>
        public const ushort ProcessingFailure = 272;

        /// <summary>
        /// The DICOM instance failed validation.
        /// </summary>
        public const ushort ValidationFailure = 43264;

        /// <summary>
        /// The DICOM instance does not belong to the specified study.
        /// </summary>
        public const ushort MismatchStudyInstanceUid = 43265;

        /// <summary>
        /// The DICOM instance already exists.
        /// </summary>
        public const ushort SopInstanceAlreadyExists = 45070;

        /// <summary>
        /// The DICOM instance is being created.
        /// </summary>
        public const ushort PendingSopInstance = 45071;

        /// <summary>
        /// The workitem instance includes conflicting uids
        /// </summary>
        public const ushort MismatchWorkitemInstanceUid = 45072;

        /// <summary>
        /// The workitem instance is not in a valid procedure step state.
        /// </summary>
        public const ushort InvalidProcedureStepState = 45073;

        /// <summary>
        /// Duplicate Tag Value is not supported in a Sequence
        /// </summary>
        public const ushort DuplicateTagValueNotSupportedInSequence = 45074;
    }
}
