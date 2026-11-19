package com.example.crossbuy_mobile

import android.content.Context
import android.graphics.Bitmap
import com.cloudwebrtc.webrtc.video.LocalVideoTrack
import com.google.mediapipe.framework.image.BitmapImageBuilder
import com.google.mediapipe.framework.image.ByteBufferExtractor
import com.google.mediapipe.tasks.core.BaseOptions
import com.google.mediapipe.tasks.vision.core.RunningMode
import com.google.mediapipe.tasks.vision.imagesegmenter.ImageSegmenter
import org.webrtc.JavaI420Buffer
import org.webrtc.VideoFrame
import java.nio.ByteOrder

/**
 * Background blur in a call (owner 2026-11-19) — the app's half of what comm-blur.js does in the browser.
 *
 * flutter_webrtc hands every camera frame of a local track to its processors before it is sent
 * (LocalVideoTrack.ExternalVideoFrameProcessing). This one:
 *   1. makes a small UPRIGHT picture of the frame (phone cameras deliver frames rotated; the selfie model wants a
 *      person standing up) and asks MediaPipe's selfie segmenter (Apache-2.0, the same model the browser uses, shipped
 *      in the app's assets — nothing is downloaded and nothing leaves the phone) where the person is;
 *   2. blurs the frame cheaply on its own Y/U/V planes (an average over 8×8 / 4×4 blocks, read back smoothly);
 *   3. keeps the original where the person is and the blurred planes elsewhere, and sends that frame instead.
 * The mask is refreshed every second frame and reused in between: segmentation is the expensive part.
 */
class BlurProcessor(context: Context) : LocalVideoTrack.ExternalVideoFrameProcessing {
    private val segmenter: ImageSegmenter = ImageSegmenter.createFromOptions(
        context,
        ImageSegmenter.ImageSegmenterOptions.builder()
            .setBaseOptions(BaseOptions.builder().setModelAssetPath("selfie_segmenter.tflite").build())
            .setRunningMode(RunningMode.VIDEO)
            .setOutputConfidenceMasks(true)
            .setOutputCategoryMask(false)
            .build()
    )

    private var mask: FloatArray? = null
    private var maskW = 0
    private var maskH = 0
    private var frameNo = 0L
    private var lastTs = 0L
    @Volatile private var closed = false

    fun close() {
        closed = true
        synchronized(this) { try { segmenter.close() } catch (_: Exception) {} }
    }

    override fun onFrame(frame: VideoFrame): VideoFrame {
        if (closed) return frame
        val src = frame.buffer.toI420() ?: return frame
        try {
            val w = src.width
            val h = src.height
            val rot = ((frame.rotation % 360) + 360) % 360
            synchronized(this) {
                if (closed) return frame
                if (mask == null || frameNo % 2L == 0L) segment(src, w, h, rot, frame.timestampNs)
            }
            frameNo++
            val m = mask ?: return frame
            return VideoFrame(compose(src, w, h, rot, m), frame.rotation, frame.timestampNs)
        } catch (_: Exception) {
            return frame                         // a dropped effect, never a dropped call
        } finally {
            src.release()
        }
    }

    // ---- sensor (x, y) in [0,1] ↔ upright (u, v) in [0,1], for the frame's clockwise rotation ----
    private fun toSensor(rot: Int, u: Float, v: Float): Pair<Float, Float> = when (rot) {
        90 -> Pair(v, 1f - u)
        180 -> Pair(1f - u, 1f - v)
        270 -> Pair(1f - v, u)
        else -> Pair(u, v)
    }

    private fun toUpright(rot: Int, x: Float, y: Float): Pair<Float, Float> = when (rot) {
        90 -> Pair(1f - y, x)
        180 -> Pair(1f - x, 1f - y)
        270 -> Pair(y, 1f - x)
        else -> Pair(x, y)
    }

    private fun segment(src: VideoFrame.I420Buffer, w: Int, h: Int, rot: Int, tsNs: Long) {
        val upW: Int; val upH: Int
        if (rot == 90 || rot == 270) { upW = h; upH = w } else { upW = w; upH = h }
        val sw = 256
        val sh = maxOf(1, (256f * upH / upW).toInt())
        val px = IntArray(sw * sh)
        val y = src.dataY; val u = src.dataU; val v = src.dataV
        val sy = src.strideY; val su = src.strideU; val sv = src.strideV
        for (j in 0 until sh) {
            for (i in 0 until sw) {
                val (fx, fy) = toSensor(rot, (i + .5f) / sw, (j + .5f) / sh)
                val xx = (fx * w).toInt().coerceIn(0, w - 1)
                val yy = (fy * h).toInt().coerceIn(0, h - 1)
                val yv = (y.get(yy * sy + xx).toInt() and 0xff) - 16
                val uv = (u.get((yy / 2) * su + xx / 2).toInt() and 0xff) - 128
                val vv = (v.get((yy / 2) * sv + xx / 2).toInt() and 0xff) - 128
                val c = 1.164f * yv
                val r = (c + 1.596f * vv).toInt().coerceIn(0, 255)
                val g = (c - 0.392f * uv - 0.813f * vv).toInt().coerceIn(0, 255)
                val b = (c + 2.017f * uv).toInt().coerceIn(0, 255)
                px[j * sw + i] = (0xff shl 24) or (r shl 16) or (g shl 8) or b
            }
        }
        val bmp = Bitmap.createBitmap(px, sw, sh, Bitmap.Config.ARGB_8888)
        val ts = maxOf(lastTs + 1, tsNs / 1_000_000)   // VIDEO mode wants increasing milliseconds
        lastTs = ts
        val result = segmenter.segmentForVideo(BitmapImageBuilder(bmp).build(), ts)
        val img = result.confidenceMasks().orElse(null)?.firstOrNull() ?: return
        val buf = ByteBufferExtractor.extract(img).order(ByteOrder.nativeOrder()).asFloatBuffer()
        val arr = FloatArray(img.width * img.height)
        buf.get(arr)
        mask = arr; maskW = img.width; maskH = img.height
        bmp.recycle()
    }

    /** The person's confidence (0..1) at a sensor position. */
    private fun maskAt(rot: Int, m: FloatArray, x: Float, y: Float): Float {
        val (u, v) = toUpright(rot, x, y)
        val i = (u * maskW).toInt().coerceIn(0, maskW - 1)
        val j = (v * maskH).toInt().coerceIn(0, maskH - 1)
        return m[j * maskW + i]
    }

    /** Average over k×k blocks of one plane, then read back with bilinear smoothing: a soft blur for little work. */
    private fun blurPlane(p: java.nio.ByteBuffer, stride: Int, pw: Int, ph: Int, k: Int): FloatArray {
        val bw = maxOf(1, pw / k); val bh = maxOf(1, ph / k)
        val small = FloatArray(bw * bh)
        for (bj in 0 until bh) for (bi in 0 until bw) {
            var s = 0; var n = 0
            var yy = bj * k
            while (yy < minOf(ph, bj * k + k)) {
                var xx = bi * k
                while (xx < minOf(pw, bi * k + k)) { s += p.get(yy * stride + xx).toInt() and 0xff; n++; xx += 2 }
                yy += 2
            }
            small[bj * bw + bi] = if (n == 0) 0f else s.toFloat() / n
        }
        // a second pass (3×3) over the small grid makes the blur round instead of blocky
        val sm = FloatArray(bw * bh)
        for (bj in 0 until bh) for (bi in 0 until bw) {
            var s = 0f; var n = 0
            for (dj in -1..1) for (di in -1..1) {
                val a = bi + di; val b = bj + dj
                if (a in 0 until bw && b in 0 until bh) { s += small[b * bw + a]; n++ }
            }
            sm[bj * bw + bi] = s / n
        }
        val out = FloatArray(pw * ph)
        for (yy in 0 until ph) {
            val fy = ((yy + .5f) / k - .5f).coerceIn(0f, (bh - 1).toFloat())
            val y0 = fy.toInt(); val y1 = minOf(bh - 1, y0 + 1); val ty = fy - y0
            for (xx in 0 until pw) {
                val fx = ((xx + .5f) / k - .5f).coerceIn(0f, (bw - 1).toFloat())
                val x0 = fx.toInt(); val x1 = minOf(bw - 1, x0 + 1); val tx = fx - x0
                val a = sm[y0 * bw + x0] * (1 - tx) + sm[y0 * bw + x1] * tx
                val b = sm[y1 * bw + x0] * (1 - tx) + sm[y1 * bw + x1] * tx
                out[yy * pw + xx] = a * (1 - ty) + b * ty
            }
        }
        return out
    }

    private fun compose(src: VideoFrame.I420Buffer, w: Int, h: Int, rot: Int, m: FloatArray): VideoFrame.Buffer {
        val cw = (w + 1) / 2; val ch = (h + 1) / 2
        val by = blurPlane(src.dataY, src.strideY, w, h, 16)
        val bu = blurPlane(src.dataU, src.strideU, cw, ch, 8)
        val bv = blurPlane(src.dataV, src.strideV, cw, ch, 8)
        val out = JavaI420Buffer.allocate(w, h)
        val oy = out.dataY; val ou = out.dataU; val ov = out.dataV
        // the mask, once per 2×2 block: it serves the four Y pixels and the one U/V pixel of the block
        for (cy in 0 until ch) {
            for (cx in 0 until cw) {
                val a = maskAt(rot, m, (cx * 2 + 1f) / w, (cy * 2 + 1f) / h).coerceIn(0f, 1f)
                for (dy in 0..1) {
                    val yy = cy * 2 + dy; if (yy >= h) continue
                    for (dx in 0..1) {
                        val xx = cx * 2 + dx; if (xx >= w) continue
                        val o = src.dataY.get(yy * src.strideY + xx).toInt() and 0xff
                        oy.put(yy * out.strideY + xx, (o * a + by[yy * w + xx] * (1 - a)).toInt().coerceIn(0, 255).toByte())
                    }
                }
                val uo = src.dataU.get(cy * src.strideU + cx).toInt() and 0xff
                val vo = src.dataV.get(cy * src.strideV + cx).toInt() and 0xff
                ou.put(cy * out.strideU + cx, (uo * a + bu[cy * cw + cx] * (1 - a)).toInt().coerceIn(0, 255).toByte())
                ov.put(cy * out.strideV + cx, (vo * a + bv[cy * cw + cx] * (1 - a)).toInt().coerceIn(0, 255).toByte())
            }
        }
        return out
    }
}
