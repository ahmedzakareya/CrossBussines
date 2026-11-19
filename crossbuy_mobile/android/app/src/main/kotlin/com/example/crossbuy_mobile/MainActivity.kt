package com.example.crossbuy_mobile

import com.cloudwebrtc.webrtc.FlutterWebRTCPlugin
import com.cloudwebrtc.webrtc.video.LocalVideoTrack
import io.flutter.embedding.android.FlutterActivity
import io.flutter.embedding.engine.FlutterEngine
import io.flutter.plugin.common.MethodChannel

class MainActivity : FlutterActivity() {
    // Background blur in a call (owner 2026-11-19): the call screen asks "crossbuy/blur" to put BlurProcessor on its
    // camera track (by the track's id) or take it off. The processor runs on the phone; nothing is sent anywhere.
    private val blurs = HashMap<String, BlurProcessor>()

    override fun configureFlutterEngine(flutterEngine: FlutterEngine) {
        super.configureFlutterEngine(flutterEngine)
        MethodChannel(flutterEngine.dartExecutor.binaryMessenger, "crossbuy/blur").setMethodCallHandler { call, result ->
            when (call.method) {
                "supported" -> result.success(true)
                "set" -> {
                    val id = call.argument<String>("trackId")
                    val on = call.argument<Boolean>("on") == true
                    val track = id?.let { FlutterWebRTCPlugin.sharedSingleton?.getLocalTrack(it) } as? LocalVideoTrack
                    if (track == null) { result.success(false); return@setMethodCallHandler }
                    try {
                        if (on && !blurs.containsKey(id)) {
                            val p = BlurProcessor(applicationContext)
                            blurs[id] = p
                            track.addProcessor(p)
                        } else if (!on) {
                            blurs.remove(id)?.let { track.removeProcessor(it); it.close() }
                        }
                        result.success(true)
                    } catch (e: Exception) {
                        blurs.remove(id)
                        result.success(false)
                    }
                }
                "clear" -> {   // the call ended: every processor goes, with its model
                    blurs.values.forEach { it.close() }
                    blurs.clear()
                    result.success(true)
                }
                else -> result.notImplemented()
            }
        }
    }
}
