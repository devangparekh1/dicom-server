﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Dicom;
using Dicom.Imaging;
using Dicom.Imaging.Codec;
using Dicom.IO.Buffer;
using EnsureThat;
using MediatR;
using Microsoft.Health.Dicom.Core.Features.Persistence;
using Microsoft.Health.Dicom.Core.Features.Persistence.Exceptions;
using Microsoft.Health.Dicom.Core.Features.Resources.Store;
using Microsoft.Health.Dicom.Core.Messages.Retrieve;

namespace Microsoft.Health.Dicom.Core.Features.Resources.Retrieve
{
    public class RetrieveDicomResourceHandler : IRequestHandler<RetrieveDicomResourceRequest, RetrieveDicomResourceResponse>
    {
        private readonly IDicomBlobDataStore _dicomBlobDataStore;
        private readonly IDicomMetadataStore _dicomMetadataStore;
        private static readonly DicomTransferSyntax DefaultTransferSyntax = DicomTransferSyntax.ExplicitVRLittleEndian;

        private static DicomTransferSyntax[] _supportedTransferSyntaxes8bit = new[]
        {
            DicomTransferSyntax.DeflatedExplicitVRLittleEndian,
            DicomTransferSyntax.ExplicitVRBigEndian,
            DicomTransferSyntax.ExplicitVRLittleEndian,
            DicomTransferSyntax.ImplicitVRLittleEndian,
            DicomTransferSyntax.JPEG2000Lossless,
            DicomTransferSyntax.JPEG2000Lossy,
            DicomTransferSyntax.JPEGProcess1,
            DicomTransferSyntax.JPEGProcess2_4,
            DicomTransferSyntax.RLELossless,
        };

        private static DicomTransferSyntax[] _supportedTransferSyntaxesOver8bit = new[]
        {
            DicomTransferSyntax.DeflatedExplicitVRLittleEndian,
            DicomTransferSyntax.ExplicitVRBigEndian,
            DicomTransferSyntax.ExplicitVRLittleEndian,
            DicomTransferSyntax.ImplicitVRLittleEndian,
            DicomTransferSyntax.RLELossless,
        };

        public RetrieveDicomResourceHandler(
            IDicomBlobDataStore dicomBlobDataStore,
            IDicomMetadataStore dicomMetadataStore)
        {
            EnsureArg.IsNotNull(dicomBlobDataStore, nameof(dicomBlobDataStore));
            EnsureArg.IsNotNull(dicomMetadataStore, nameof(dicomMetadataStore));

            _dicomBlobDataStore = dicomBlobDataStore;
            _dicomMetadataStore = dicomMetadataStore;
        }

        private bool CanTranscodeDataset(DicomDataset ds, DicomTransferSyntax toTransferSyntax)
        {
            if (toTransferSyntax == null)
            {
               return true;
            }

            var fromTs = ds.InternalTransferSyntax;
            ds.TryGetSingleValue(DicomTag.BitsAllocated, out ushort bpp);
            ds.TryGetString(DicomTag.PhotometricInterpretation, out string photometricInterpretation);

            // Bug in fo-dicom 4.0.1
            if ((toTransferSyntax == DicomTransferSyntax.JPEGProcess1 || toTransferSyntax == DicomTransferSyntax.JPEGProcess2_4) &&
                ((photometricInterpretation == PhotometricInterpretation.Monochrome2.Value) ||
                 (photometricInterpretation == PhotometricInterpretation.Monochrome1.Value)))
            {
                return false;
            }

            if (((bpp > 8) && _supportedTransferSyntaxesOver8bit.Contains(toTransferSyntax) && _supportedTransferSyntaxesOver8bit.Contains(fromTs)) ||
                 ((bpp <= 8) && _supportedTransferSyntaxes8bit.Contains(toTransferSyntax) && _supportedTransferSyntaxes8bit.Contains(fromTs)))
            {
                return true;
            }

            return false;
        }

        private bool IsOriginalTransferSyntaxRequested(string transferSyntax)
        {
            return transferSyntax.Equals("*", StringComparison.InvariantCultureIgnoreCase);
        }

        public async Task<RetrieveDicomResourceResponse> Handle(
            RetrieveDicomResourceRequest message, CancellationToken cancellationToken)
        {
            try
            {
                IEnumerable<DicomInstance> instancesToRetrieve;

                switch (message.ResourceType)
                {
                    case ResourceType.Frames:
                    case ResourceType.Instance:
                        instancesToRetrieve = new[] { new DicomInstance(message.StudyInstanceUID, message.SeriesInstanceUID, message.SopInstanceUID) };

                        break;
                    case ResourceType.Series:
                        instancesToRetrieve = await _dicomMetadataStore.GetInstancesInSeriesAsync(
                            message.StudyInstanceUID,
                            message.SeriesInstanceUID,
                            cancellationToken);
                        break;
                    case ResourceType.Study:
                        instancesToRetrieve = await _dicomMetadataStore.GetInstancesInStudyAsync(message.StudyInstanceUID, cancellationToken);
                        break;
                    default:
                        throw new ArgumentException($"Unknown retrieve transaction type: {message.ResourceType}", nameof(message));
                }

                Stream[] resultStreams = await Task.WhenAll(
                                                    instancesToRetrieve.Select(
                                                        x => _dicomBlobDataStore.GetFileAsStreamAsync(
                                                            StoreDicomResourcesHandler.GetBlobStorageName(x), cancellationToken)));

                DicomTransferSyntax parsedDicomTransferSyntax =
                                                    message.OriginalTransferSyntaxRequested() ?
                                                    null :
                                                    string.IsNullOrWhiteSpace(message.RequestedTransferSyntax) ?
                                                    DefaultTransferSyntax :
                                                    DicomTransferSyntax.Parse(message.RequestedTransferSyntax);

                var responseCode = HttpStatusCode.OK;

                if (message.ResourceType == ResourceType.Frames)
                {
                    // We first validate the file has the requested frames, then pass the frame for lazy encoding.
                    var dicomFile = DicomFile.Open(resultStreams.Single());
                    ValidateHasFrames(dicomFile, message.Frames);

                    if (!message.OriginalTransferSyntaxRequested() && !CanTranscodeDataset(dicomFile.Dataset, parsedDicomTransferSyntax))
                    {
                        throw new DataStoreException(HttpStatusCode.NotAcceptable);
                    }

                    // Note that per DICOMWeb spec (http://dicom.nema.org/medical/dicom/current/output/html/part18.html#sect_9.5.1.2.1)
                    // frame number in the UIR is 1-based, unlike fo-dicom representation
                    resultStreams = message.Frames.Select(
                        x => new LazyTransformStream<DicomFile>(dicomFile, y => GetFrame(y, x - 1, parsedDicomTransferSyntax)))
                        .ToArray();
                }
                else
                {
                    if (!message.OriginalTransferSyntaxRequested())
                    {
                        resultStreams = resultStreams.Where(x =>
                        {
                            var canTranscode = false;

                            try
                            {
                                var dicomFile = DicomFile.Open(x, FileReadOption.ReadLargeOnDemand);
                                canTranscode = CanTranscodeDataset(dicomFile.Dataset, parsedDicomTransferSyntax);
                            }
                            catch (DicomFileException)
                            {
                                canTranscode = false;
                            }

                            x.Seek(0, SeekOrigin.Begin);

                            // If some of the instances are not transcodeable, Partial Content should be returned
                            if (!canTranscode)
                            {
                                responseCode = HttpStatusCode.PartialContent;
                            }

                            return canTranscode;
                        }).ToArray();
                    }

                    if (resultStreams.Length == 0)
                    {
                        throw new DataStoreException(HttpStatusCode.NotAcceptable);
                    }

                    resultStreams = resultStreams.Select(x => new LazyTransformStream<Stream>(x, y => EncodeDicomFile(y, parsedDicomTransferSyntax))).ToArray();
                }

                return new RetrieveDicomResourceResponse(responseCode, resultStreams);
            }
            catch (DataStoreException e)
            {
                return new RetrieveDicomResourceResponse(e.StatusCode);
            }
        }

        private static Stream EncodeDicomFile(Stream stream, DicomTransferSyntax requestedTransferSyntax)
        {
            var tempDicomFile = DicomFile.Open(stream);

            // If the DICOM file is already in the requested transfer syntax, return the base stream, otherwise re-encode.
            if (tempDicomFile.Dataset.InternalTransferSyntax == requestedTransferSyntax)
            {
                stream.Seek(0, SeekOrigin.Begin);
                return stream;
            }
            else
            {
                if (requestedTransferSyntax != null)
                {
                    try
                    {
                        var transcoder = new DicomTranscoder(
                            tempDicomFile.Dataset.InternalTransferSyntax,
                            requestedTransferSyntax);
                        tempDicomFile = transcoder.Transcode(tempDicomFile);
                    }

                    // We catch all here as Transcoder can throw a wide variety of things.
                    // Basically this means codec failure - a quite extraordinary situation, but not impossible
                    catch
                    {
                        tempDicomFile = null;
                    }
                }

                var resultStream = new MemoryStream();

                if (tempDicomFile != null)
                {
                    tempDicomFile.Save(resultStream);
                    resultStream.Seek(0, SeekOrigin.Begin);
                }

                // We can dispose of the base stream as this is not needed.
                stream.Dispose();
                return resultStream;
            }
        }

        private static Stream GetFrame(DicomFile dicomFile, int frame, DicomTransferSyntax requestedTransferSyntax)
        {
            DicomDataset dataset = dicomFile.Dataset;
            IByteBuffer resultByteBuffer;

            if (dataset.InternalTransferSyntax.IsEncapsulated && (requestedTransferSyntax != null))
            {
                // Decompress single frame from source dataset
                var transcoder = new DicomTranscoder(dataset.InternalTransferSyntax, requestedTransferSyntax);
                resultByteBuffer = transcoder.DecodeFrame(dataset, frame);
            }
            else
            {
                // Pull uncompressed frame from source pixel data
                var pixelData = DicomPixelData.Create(dataset);
                if (frame >= pixelData.NumberOfFrames)
                {
                    throw new DataStoreException(HttpStatusCode.NotFound, new ArgumentException($"The frame '{frame}' does not exist.", nameof(frame)));
                }

                resultByteBuffer = pixelData.GetFrame(frame);
            }

            return new MemoryStream(resultByteBuffer.Data);
        }

        private static void ValidateHasFrames(DicomFile dicomFile, IEnumerable<int> frames)
        {
            DicomDataset dataset = dicomFile.Dataset;

            // Validate the dataset has the correct DICOM tags.
            if (!dataset.Contains(DicomTag.BitsAllocated) ||
                !dataset.Contains(DicomTag.Columns) ||
                !dataset.Contains(DicomTag.Rows) ||
                !dataset.Contains(DicomTag.PixelData))
            {
                throw new DataStoreException(HttpStatusCode.NotFound);
            }

            var pixelData = DicomPixelData.Create(dataset);
            var missingFrames = frames.Where(x => x > pixelData.NumberOfFrames || x < 0).ToArray();

            // If any missing frames, throw not found exception for the specific frames not found.
            if (missingFrames.Length > 0)
            {
                throw new DataStoreException(HttpStatusCode.NotFound, new ArgumentException($"The frame(s) '{string.Join(", ", missingFrames)}' do not exist.", nameof(frames)));
            }
        }
    }
}
