import time

from PyQt6.QtCore import (
    QParallelAnimationGroup,
    QPoint,
    QPropertyAnimation,
    Qt,
    QObject,
    pyqtSignal as Signal,
)
from PyQt6.QtGui import QAction
from PyQt6.QtWidgets import QFrame, QGraphicsOpacityEffect, QVBoxLayout, QWidget
from qfluentwidgets import (
    BreadcrumbBar,
    CaptionLabel,
    ComboBox,
    ScrollArea,
    SmoothMode,
    TitleLabel,
)
from qfluentwidgets.common.animation import FluentAnimation
from qfluentwidgets.common.smooth_scroll import SmoothMode as NativeSmoothMode
from qfluentwidgets.components.widgets.combo_box import ComboBoxMenu
from qfluentwidgets.components.widgets.menu import MenuAnimationType


class Page(ScrollArea):
    def __init__(self, object_name, title, subtitle):
        super().__init__()
        self.setObjectName(object_name)
        self._configure_scroll_behavior()
        self.view = QWidget()
        self.view.setStyleSheet("background: transparent;")
        self.layout = QVBoxLayout(self.view)
        self.layout.setContentsMargins(32, 28, 32, 32)
        self.layout.setSpacing(18)
        self.layout.addWidget(TitleLabel(title))
        self.layout.addWidget(CaptionLabel(subtitle))
        self.breadcrumb_bar = BreadcrumbBar()
        self.breadcrumb_bar.setObjectName(f"{object_name}Breadcrumb")
        self.layout.addWidget(self.breadcrumb_bar)
        self.setWidget(self.view)
        self.setWidgetResizable(True)
        self.setFrameShape(QFrame.Shape.NoFrame)
        self.set_breadcrumbs([])

    def _configure_scroll_behavior(self):
        self.setSmoothMode(SmoothMode.NO_SMOOTH, Qt.Orientation.Vertical)
        self.setSmoothMode(SmoothMode.NO_SMOOTH, Qt.Orientation.Horizontal)
        self.setHorizontalScrollBarPolicy(Qt.ScrollBarPolicy.ScrollBarAlwaysOff)
        self.setVerticalScrollBarPolicy(Qt.ScrollBarPolicy.ScrollBarAsNeeded)

    def set_breadcrumbs(self, crumbs):
        self.breadcrumb_bar.blockSignals(True)
        self.breadcrumb_bar.clear()
        if not crumbs:
            self.breadcrumb_bar.setVisible(False)
            self.breadcrumb_bar.blockSignals(False)
            return

        self.breadcrumb_bar.setVisible(True)
        for index, crumb in enumerate(crumbs):
            route_key = crumb.get("route_key") or f"breadcrumb_{index}"
            self.breadcrumb_bar.addItem(route_key, crumb.get("label", ""))
        self.breadcrumb_bar.setCurrentIndex(len(crumbs) - 1)
        self.breadcrumb_bar.blockSignals(False)


class P2PEventBridge(QObject):
    status = Signal(str)
    stopped = Signal(str)
    failed = Signal(str)


class NativeComboBoxMenu(ComboBoxMenu):
    def __init__(self, parent=None):
        super().__init__(parent)
        if hasattr(self.view, "scrollDelegate"):
            self.view.scrollDelegate.verticalSmoothScroll.setSmoothMode(NativeSmoothMode.NO_SMOOTH)
            self.view.scrollDelegate.horizonSmoothScroll.setSmoothMode(NativeSmoothMode.NO_SMOOTH)


class NativeComboBox(ComboBox):
    def _createComboMenu(self):
        return NativeComboBoxMenu(self)

    def _menu_min_width(self):
        text_width = 0
        for index in range(self.count()):
            text_width = max(text_width, self.fontMetrics().horizontalAdvance(self.itemText(index)))
        return max(160, min(max(text_width + 56, 180), 640))

    def _showComboMenu(self):
        if not self.items:
            return

        menu = self._createComboMenu()
        for index, item in enumerate(self.items):
            action = QAction(item.icon, item.text, triggered=lambda checked=False, i=index: self._onItemClicked(i))
            action.setEnabled(item.isEnabled)
            menu.addAction(action)

        menu.view.setMinimumWidth(self._menu_min_width())
        menu.view.adjustSize()
        menu.adjustSize()
        menu.setMaxVisibleItems(self.maxVisibleItems())
        menu.setAttribute(Qt.WidgetAttribute.WA_DeleteOnClose)
        menu.closedSignal.connect(self._onDropMenuClosed)
        self.dropMenu = menu

        if self.currentIndex() >= 0 and menu.actions():
            menu.setDefaultAction(menu.actions()[self.currentIndex()])

        below = self.mapToGlobal(QPoint(0, self.height()))
        above = self.mapToGlobal(QPoint(0, 0))
        drop_height = menu.view.heightForAnimation(below, MenuAnimationType.DROP_DOWN)
        pull_height = menu.view.heightForAnimation(above, MenuAnimationType.PULL_UP)
        if drop_height >= pull_height:
            menu.view.adjustSize(below, MenuAnimationType.DROP_DOWN)
            menu.exec(below, aniType=MenuAnimationType.DROP_DOWN)
        else:
            menu.view.adjustSize(above, MenuAnimationType.PULL_UP)
            menu.exec(above, aniType=MenuAnimationType.PULL_UP)

    def reset_items(self, texts, current_text="", current_index=None):
        self._closeComboMenu()
        was_blocked = self.blockSignals(True)
        self.clear()
        self.addItems(list(texts or []))
        if current_index is not None:
            self.setCurrentIndex(current_index)
        elif current_text:
            self.setCurrentText(current_text)
        self.blockSignals(was_blocked)


class UiMotionController(QObject):
    def __init__(self, parent=None):
        super().__init__(parent)
        self.animations = []
        self.last_trigger_at = {}

    def _track(self, animation):
        self.animations.append(animation)

        def cleanup():
            try:
                self.animations.remove(animation)
            except ValueError:
                pass

        if isinstance(animation, QParallelAnimationGroup):
            animation.finished.connect(cleanup)
        else:
            animation.finished.connect(cleanup)
        return animation

    def _opacity_effect(self, widget):
        effect = widget.graphicsEffect()
        if isinstance(effect, QGraphicsOpacityEffect):
            effect.setEnabled(True)
            return effect

        effect = QGraphicsOpacityEffect(widget)
        effect.setOpacity(0.0)
        effect.setEnabled(False)
        widget.setGraphicsEffect(effect)
        effect.setEnabled(True)
        return effect

    def _finish_with_effect(self, animation, effect):
        def cleanup_effect():
            effect.setOpacity(1.0)
            effect.setEnabled(False)

        animation.finished.connect(cleanup_effect)

    def _curve_accelerate(self):
        return FluentAnimation.createBezierCurve(0.18, 0.0, 0.0, 1.0)

    def _curve_decelerate(self):
        return FluentAnimation.createBezierCurve(0.12, 0.82, 0.22, 1.0)

    def _curve_emphasized(self):
        return FluentAnimation.createBezierCurve(0.2, 0.0, 0.0, 1.0)

    def fade_slide_in(self, widget, offset=18, duration=320):
        if widget is None:
            return

        effect = self._opacity_effect(widget)
        effect.setOpacity(0.0)

        opacity_animation = QPropertyAnimation(effect, b"opacity", widget)
        opacity_animation.setDuration(duration)
        opacity_animation.setStartValue(0.0)
        opacity_animation.setEndValue(1.0)
        opacity_animation.setEasingCurve(self._curve_accelerate())
        self._finish_with_effect(opacity_animation, effect)

        group = QParallelAnimationGroup(widget)
        group.addAnimation(opacity_animation)
        self._track(group).start()

    def cross_fade_stack(self, stack, index, duration=260):
        if stack is None or index < 0 or index >= stack.count():
            return

        current = stack.currentWidget()
        target = stack.widget(index)
        if target is None or current is target:
            stack.setCurrentIndex(index)
            return

        effect = self._opacity_effect(target)

        effect.setOpacity(0.0)
        stack.setCurrentIndex(index)

        opacity_animation = QPropertyAnimation(effect, b"opacity", target)
        opacity_animation.setDuration(duration)
        opacity_animation.setStartValue(0.0)
        opacity_animation.setEndValue(1.0)
        opacity_animation.setEasingCurve(self._curve_accelerate())
        self._finish_with_effect(opacity_animation, effect)

        group = QParallelAnimationGroup(target)
        group.addAnimation(opacity_animation)
        self._track(group).start()

    def pulse_list(self, list_widget, duration=220):
        if list_widget is None:
            return

        effect = self._opacity_effect(list_widget)
        effect.setOpacity(0.24)

        animation = QPropertyAnimation(effect, b"opacity", list_widget)
        animation.setDuration(duration)
        animation.setStartValue(0.24)
        animation.setEndValue(1.0)
        animation.setEasingCurve(self._curve_decelerate())
        self._finish_with_effect(animation, effect)
        self._track(animation).start()

    def pulse_widget(self, widget, duration=220, start_opacity=0.55, throttle_key=None, min_interval=0.0):
        if widget is None:
            return

        if throttle_key:
            now = time.monotonic()
            previous = self.last_trigger_at.get(throttle_key, 0.0)
            if now - previous < min_interval:
                return
            self.last_trigger_at[throttle_key] = now

        effect = self._opacity_effect(widget)
        effect.setOpacity(start_opacity)

        animation = QPropertyAnimation(effect, b"opacity", widget)
        animation.setDuration(duration)
        animation.setStartValue(start_opacity)
        animation.setEndValue(1.0)
        animation.setEasingCurve(self._curve_decelerate())
        self._finish_with_effect(animation, effect)
        self._track(animation).start()
