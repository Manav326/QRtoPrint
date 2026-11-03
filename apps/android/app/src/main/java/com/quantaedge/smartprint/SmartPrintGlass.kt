package com.quantaedge.smartprint

import android.content.Context
import android.graphics.Color
import android.graphics.drawable.Drawable
import android.graphics.drawable.GradientDrawable
import android.graphics.drawable.LayerDrawable
import android.os.Build
import eightbitlab.com.blurview.BlurTarget
import eightbitlab.com.blurview.BlurView

/** Native glass materials adapted from BajajDealership's Liquid Glass edition. */
object SmartPrintGlass {
    fun backdrop(context: Context): Drawable {
        val base = GradientDrawable(
            GradientDrawable.Orientation.TL_BR,
            intArrayOf(context.getColor(R.color.qp_surface), context.getColor(R.color.qp_surface))
        )
        val warm = radialGlow(context, R.color.qp_glass_warm_glow, 340, 0.88f, 0.08f)
        val cool = radialGlow(context, R.color.qp_glass_cool_glow, 390, 0.08f, 0.86f)
        return LayerDrawable(arrayOf(base, cool, warm))
    }

    fun surface(context: Context, radiusDp: Int = 20, fillColor: Int = R.color.qp_glass_card) =
        GradientDrawable().apply {
            setColor(context.getColor(fillColor))
            cornerRadius = context.dp(radiusDp).toFloat()
            setStroke(context.dp(1), context.getColor(R.color.qp_glass_border))
        }

    fun edge(context: Context) = GradientDrawable().apply {
        setColor(Color.TRANSPARENT)
        setStroke(context.dp(1), context.getColor(R.color.qp_glass_edge))
    }

    fun bindBlur(view: BlurView, target: BlurTarget, context: Context, overlay: Int, backdrop: Drawable) {
        view.setupWith(target)
            .setFrameClearDrawable(backdrop)
            .setBlurRadius(if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) 24f else 18f)
            .setOverlayColor(context.getColor(overlay))
    }

    private fun radialGlow(context: Context, color: Int, radius: Int, x: Float, y: Float) =
        GradientDrawable().apply {
            gradientType = GradientDrawable.RADIAL_GRADIENT
            gradientRadius = context.dp(radius).toFloat()
            setGradientCenter(x, y)
            setColors(intArrayOf(context.getColor(color), Color.TRANSPARENT))
        }

    private fun Context.dp(value: Int): Int = (value * resources.displayMetrics.density).toInt()
}
