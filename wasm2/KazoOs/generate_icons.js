const fs = require('fs');
const path = require('path');
const zlib = require('zlib');

function createPng(width, height, drawFn) {
    const buffer = Buffer.alloc(width * height * 4);
    drawFn(buffer, width, height);

    // PNG signature
    const signature = Buffer.from([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

    // IHDR chunk
    const ihdr = Buffer.alloc(13);
    ihdr.writeUInt32BE(width, 0);
    ihdr.writeUInt32BE(height, 4);
    ihdr[8] = 8; // Bit depth
    ihdr[9] = 6; // Color type (RGBA)
    ihdr[10] = 0; // Compression
    ihdr[11] = 0; // Filter
    ihdr[12] = 0; // Interlace
    const ihdrChunk = createChunk('IHDR', ihdr);

    // IDAT chunk (raw scanlines with filter byte 0)
    const scanlineLength = width * 4 + 1;
    const rawData = Buffer.alloc(height * scanlineLength);
    for (let y = 0; y < height; y++) {
        rawData[y * scanlineLength] = 0; // None filter
        buffer.copy(rawData, y * scanlineLength + 1, y * width * 4, (y + 1) * width * 4);
    }
    const compressed = zlib.deflateSync(rawData);
    const idatChunk = createChunk('IDAT', compressed);

    // IEND chunk
    const iendChunk = createChunk('IEND', Buffer.alloc(0));

    return Buffer.concat([signature, ihdrChunk, idatChunk, iendChunk]);
}

function createChunk(type, data) {
    const length = data.length;
    const chunk = Buffer.alloc(4 + 4 + length + 4);
    chunk.writeUInt32BE(length, 0);
    chunk.write(type, 4, 4, 'ascii');
    data.copy(chunk, 8);
    const crc = crc32(chunk.subarray(4, 8 + length));
    chunk.writeUInt32BE(crc, 8 + length);
    return chunk;
}

// Simple CRC32 implementation for PNG chunks
function crc32(buf) {
    let crc = 0xFFFFFFFF;
    for (let i = 0; i < buf.length; i++) {
        crc ^= buf[i];
        for (let j = 0; j < 8; j++) {
            crc = (crc >>> 1) ^ (crc & 1 ? 0xEDB88320 : 0);
        }
    }
    return (crc ^ 0xFFFFFFFF) >>> 0;
}

function drawKazoOsLogo(buffer, width, height) {
    const cx = width / 2;
    const cy = height / 2;
    const radius = width * 0.45;

    for (let y = 0; y < height; y++) {
        for (let x = 0; x < width; x++) {
            const idx = (y * width + x) * 4;
            const dx = x - cx;
            const dy = y - cy;
            const dist = Math.sqrt(dx * dx + dy * dy);

            // Dark background #0d1117
            let r = 13, g = 17, b = 23, a = 255;

            // Rounded circle container
            if (dist <= radius) {
                // Surface color #161b22 with border
                if (dist > radius - width * 0.03) {
                    // Accent border #42d392
                    r = 66; g = 211; b = 146;
                } else {
                    r = 22; g = 27; b = 34;
                }

                // Render terminal ">_" prompt in center
                const relX = (x - cx) / width;
                const relY = (y - cy) / height;

                // Chevron >
                const inChevron = (relX >= -0.25 && relX <= -0.05) &&
                    (Math.abs(relY - (relX + 0.15)) < 0.04 || Math.abs(relY + (relX + 0.15)) < 0.04);

                // Cursor _
                const inCursor = (relX >= 0.05 && relX <= 0.25) && (relY >= 0.12 && relY <= 0.20);

                if (inChevron || inCursor) {
                    r = 66; g = 211; b = 146; // #42d392 accent green
                }
            } else {
                a = 0; // Transparent corners
            }

            buffer[idx] = r;
            buffer[idx + 1] = g;
            buffer[idx + 2] = b;
            buffer[idx + 3] = a;
        }
    }
}

const outDir = path.join(__dirname, 'OS', 'Kazo.Os.Ecosystem', 'wwwroot', 'icons');
if (!fs.existsSync(outDir)) {
    fs.mkdirSync(outDir, { recursive: true });
}

fs.writeFileSync(path.join(outDir, 'icon-192.png'), createPng(192, 192, drawKazoOsLogo));
fs.writeFileSync(path.join(outDir, 'icon-512.png'), createPng(512, 512, drawKazoOsLogo));
console.log('Successfully generated icon-192.png and icon-512.png');
