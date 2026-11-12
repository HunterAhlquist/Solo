---
title: Notifications
lede: Get told when someone assigns work to you, and when your work is due, in the app and by email.
---

## What notifies you

| Event | When you get the notification |
| --- | --- |
| Someone assigns you to a task or an epic | Immediately. You do not get a notification when you assign yourself. |
| A task assigned to you has a deadline | Two weeks before the deadline, and again one week before. Both go out at 9:00 in your time zone. |

A finished task, or a task on a closed board, sends no deadline notifications.

If a task gets its deadline, or you get the task, less than two weeks before the deadline, the next notification tells you the number of days left. If less than one week is left, you get only one notification.

If you change the deadline, you get new notifications for the new date.

Each notification has a short title, such as "Alex assigned you a task" or "GAME-SP-12 is due in 1 week", and a line under it that tells you more. Solo picks the wording from a set of lines, some of them playful, so two notifications of the same kind can read differently. A notification keeps the wording that it got when Solo sent it.

## The bell

The bell in the app bar holds your notifications. A number on the bell shows how many you have not read.

- Select a notification to open its task. Solo marks the notification as read.
- Select **Mark all as read** to mark all of them as read.

When you leave a project, its notifications go away from the bell. Solo deletes notifications after 90 days.

## Notifications on your device

While Solo is open, a new notification also shows as a notification from your browser or the desktop app. If you are looking at Solo, it shows at the bottom of the page instead.

To turn on device notifications:

1. Open the account menu and select **Notifications**.
2. Select **Allow notifications**.
3. Accept the prompt from your browser.

If your browser blocks notifications for the site, change the setting in your browser. Solo cannot show the prompt again.

When Solo is closed, your browser and the desktop app get no notifications. The bell holds them for the next time that you open Solo.

### On iPhone and iPad

The Solo app for iPhone and iPad shows notifications as well. Turn them on the same way: **Notifications** in the account menu, **Allow notifications**, then accept the prompt. If you select **Don't Allow**, turn notifications on for Solo in the Settings app.

- **Deadline notifications** reach you even when the app is closed. The app schedules the deadline notifications that are still to come, at 9:00 in your time zone, each time that you open it and each time that it checks your instances in the background.
- **Assignments** show while the app is open. While the app is closed, it checks your instances in the background from time to time and shows new assignments then. You also get them in your email digest, and on the bell.
- Select a notification to open its task. If the notification is from another instance in your list, the app opens that instance first.

iOS decides when the app can check in the background. It is often a few times each day, and it can be less if you do not use the app much or if Low Power Mode is on. Thus a notification for an assignment can arrive some hours late. The app checks in the background only when:

- **Background App Refresh** is on for Solo in the Settings app.
- You did not close the app from the app switcher. If you swipe the app up to close it, it does not check again until you open it.

When you sign out, or remove an instance from your list, the app stops checking that instance and removes its scheduled notifications.

Nothing goes through a push service: the app gets its notifications from your instance and shows or schedules them on the device. If a deadline changes, or you finish a task, while the app is closed, a notification that the app scheduled before can still arrive. The next time that the app checks, or that you open it, it updates the schedule.

## Email digest

If the instance sends email, Solo also sends you a digest. A digest never goes out empty, and it does not repeat a notification that you already read in the app.

| Digest | What it holds | When it goes out |
| --- | --- | --- |
| **Daily** | Your overdue tasks, your tasks due in the next 2 weeks, and the work assigned to you since the last digest. The earliest deadline is first. | Each day, at the hour that you select. |
| **Weekly** | The same, once a week. | On the day and at the hour that you select. |
| **As it happens** | Your new notifications. | At most every 15 minutes. |
| **Off** | Nothing. | Never. |

A new account starts with **Daily** at 8:00. Each task in a digest links to the task in Solo.

To stop the digest from the email, use the unsubscribe button of your mail app, or follow the link at the bottom of the digest.

## Choose what notifies you

1. Open the account menu and select **Notifications**.
2. For each event, set **Notify me** for the bell and your device, and **Email me** for the digest.
3. Select how often the **Email digest** goes out, and when.

The dialog also has your **Time zone**, for deadline notifications and the digest. Solo takes it from your browser the first time that you sign in. To change it, enter the name of a city in the zone, for example `Los_Angeles`.

If the instance does not send email, the dialog has no **Email me** column, and says so under **Email digest**.
